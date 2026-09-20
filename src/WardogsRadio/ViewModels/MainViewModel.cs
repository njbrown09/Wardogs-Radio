using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WardogsRadio.Audio;
using WardogsRadio.Interop;
using WardogsRadio.Setup;

namespace WardogsRadio.ViewModels;

public enum SetupStage { Intro, Downloading, Installing, RebootNeeded, Error }

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Settings _settings = Settings.Load();
    private readonly RadioEngine _engine = new();
    private readonly MicPreview _preview = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _keyTimer;
    private CableEndpoints? _cable;
    private bool _refreshing;
    private bool _autoStartArmed;
    private bool _brandingAttempted;
    private bool _pttHeld;
    private int _ticks;

    // ---- screen -----------------------------------------------------------------------------
    [ObservableProperty] private bool _isSetupMode;
    [ObservableProperty] private SetupStage _stage = SetupStage.Intro;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _setupMessage = "";
    [ObservableProperty] private string _setupDetail = "";
    [ObservableProperty] private bool _setupBusy;

    // ---- radio ------------------------------------------------------------------------------
    public ObservableCollection<MicDevice> Microphones { get; } = new();
    public ObservableCollection<AudioApp> Apps { get; } = new();
    [ObservableProperty] private MicDevice? _selectedMic;
    [ObservableProperty] private AudioApp? _selectedApp;
    [ObservableProperty] private bool _isOnAir;
    [ObservableProperty] private float _micLevel;
    [ObservableProperty] private float _appLevel;
    [ObservableProperty] private double _micVolume = 100;
    [ObservableProperty] private double _musicVolume = 60;
    [ObservableProperty] private double _headsetVolume = 100;
    [ObservableProperty] private string _radioMicName = AudioDevices.RadioMicName;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _infoMessage;
    [ObservableProperty] private bool _needsBranding;

    // ---- options ----------------------------------------------------------------------------
    [ObservableProperty] private bool _pushToTalk;
    [ObservableProperty] private int _pushToTalkKey;
    [ObservableProperty] private bool _isCapturingKey;
    [ObservableProperty] private bool _ducking;
    [ObservableProperty] private bool _micIsLive = true;

    public string PushToTalkKeyName => IsCapturingKey ? "Press a key…" : Hotkey.Name(PushToTalkKey);
    public bool HasApps => Apps.Count > 0;
    public bool CanStart => SelectedMic != null && SelectedApp != null && _cable != null;
    public string Version => "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    public MainViewModel()
    {
        MicVolume = Math.Clamp(_settings.MicGain * 100, 0, 200);
        MusicVolume = Math.Clamp(_settings.AppGain * 100, 0, 100);
        HeadsetVolume = Math.Clamp(_settings.HeadsetVolume * 100, 2, 100);
        PushToTalk = _settings.PushToTalk;
        PushToTalkKey = _settings.PushToTalkKey;
        Ducking = _settings.Ducking;
        _engine.MicGain = (float)(MicVolume / 100);
        _engine.AppGain = (float)(MusicVolume / 100);
        _engine.LocalVolume = (float)(HeadsetVolume / 100);
        _engine.Ducking = Ducking;
        _engine.MicOpen = !PushToTalk;
        _engine.Faulted += (_, msg) => Application.Current.Dispatcher.BeginInvoke(() => { StopRadio(); ErrorMessage = msg; });

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTimer.Tick += (_, _) => TickMeters();
        _keyTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
        _keyTimer.Tick += (_, _) => TickKeys();

        Apps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasApps));
        Initialize();
    }

    private void Initialize()
    {
        VbCableInstaller.RestoreDefaultsIfHijacked(_settings);
        _cable = AudioDevices.FindCable();
        if (_cable == null)
        {
            IsSetupMode = true;
            Stage = SetupStage.Intro;
            return;
        }
        EnterRadioMode();
    }

    private void EnterRadioMode()
    {
        IsSetupMode = false;
        RecheckCable();
        _autoStartArmed = _settings.AutoStart;
        RefreshMicrophones();
        _refreshTimer.Start();
        _meterTimer.Start();
        _keyTimer.Start();
        _ = RefreshAsync();

        // Cable was already there (e.g. the user had VB-CABLE before) but still has its factory name.
        // Name it without being asked; the only thing the user sees is the UAC prompt.
        if (NeedsBranding && !_brandingAttempted)
        {
            _brandingAttempted = true;
            _ = FinishBrandingAsync();
        }
    }

    // ---- setup flow -------------------------------------------------------------------------

    [RelayCommand]
    private async Task BeginSetupAsync()
    {
        if (SetupBusy) return;
        SetupBusy = true;
        ErrorMessage = null;
        try
        {
            Stage = SetupStage.Downloading;
            SetupMessage = "Downloading the virtual microphone…";
            SetupDetail = "About 1 MB, from vb-audio.com";
            await VbCableInstaller.DownloadAsync(new Progress<double>(p => DownloadProgress = p), CancellationToken.None);

            VbCableInstaller.SnapshotDefaults(_settings);

            Stage = SetupStage.Installing;
            SetupMessage = "Windows will ask for permission. Click Yes.";
            SetupDetail = VbCableInstaller.SilentInstall
                ? "If Windows asks whether to install the driver, click Install."
                : "Then a small VB-CABLE window opens. Click \"Install Driver\". If Windows asks about the driver, click Install. When it says done, click OK.";

            var proc = VbCableInstaller.LaunchElevatedSetup();
            if (proc == null)
            {
                Stage = SetupStage.Error;
                SetupMessage = "Setup needs that permission to continue.";
                SetupDetail = "Click Try again and choose Yes when Windows asks.";
                return;
            }
            await proc.WaitForExitAsync();
            var code = proc.ExitCode;

            _cable = AudioDevices.FindCable();
            VbCableInstaller.RestoreDefaultsIfHijacked(_settings);

            if (_cable != null)
            {
                _brandingAttempted = true; // the helper already tried; don't prompt twice
                EnterRadioMode();
                return;
            }
            if (code == VbCableInstaller.ExitCodes.RebootRequired)
            {
                Stage = SetupStage.RebootNeeded;
                SetupMessage = "Almost done. Windows needs a restart to finish.";
                SetupDetail = "Wardogs Radio will open again by itself after the restart.";
                return;
            }
            Stage = SetupStage.Error;
            SetupMessage = code switch
            {
                VbCableInstaller.ExitCodes.PackageMissing => "The download looks incomplete.",
                VbCableInstaller.ExitCodes.NotAdmin => "Setup was not allowed to run as administrator.",
                _ => "The virtual microphone did not install.",
            };
            SetupDetail = "Click Try again. If it keeps failing, restart your PC and open Wardogs Radio again.";
        }
        catch (Exception ex)
        {
            Stage = SetupStage.Error;
            SetupMessage = "Setup hit a problem.";
            SetupDetail = ex.Message;
        }
        finally { SetupBusy = false; }
    }

    [RelayCommand]
    private void RebootNow() => VbCableInstaller.RebootAndRelaunch();

    [RelayCommand]
    private void OpenVbAudio() => Process.Start(new ProcessStartInfo(VbCableInstaller.HomePage) { UseShellExecute = true });

    [RelayCommand]
    private async Task FinishBrandingAsync()
    {
        if (_cable == null || SetupBusy) return;
        SetupBusy = true;
        ErrorMessage = null;
        try
        {
            var proc = VbCableInstaller.LaunchElevatedSetup();
            if (proc == null)
            {
                ErrorMessage = "Windows asked for permission and it was declined. Click \"Name it now\" and choose Yes.";
                return;
            }
            await proc.WaitForExitAsync();
            RecheckCable();
            if (NeedsBranding)
                ErrorMessage = "Could not rename the microphone (code " + proc.ExitCode + "). The radio still works; in the game pick \"" + RadioMicName + "\".";
        }
        finally { SetupBusy = false; }
    }

    private void RecheckCable()
    {
        _cable = AudioDevices.FindCable();
        NeedsBranding = _cable != null && !_cable.IsBranded;
        RadioMicName = _cable?.CaptureName ?? AudioDevices.RadioMicName;
        OnPropertyChanged(nameof(CanStart));
    }

    // ---- radio ------------------------------------------------------------------------------

    [RelayCommand]
    private void ToggleRadio()
    {
        if (IsOnAir) { StopRadio(); _autoStartArmed = false; }
        else StartRadio();
    }

    private void StartRadio()
    {
        ErrorMessage = null;
        if (SelectedMic == null) { ErrorMessage = "Pick your microphone first."; return; }
        if (SelectedApp == null) { ErrorMessage = "Pick the app you want on the radio."; return; }
        _cable ??= AudioDevices.FindCable();
        if (_cable == null) { IsSetupMode = true; Stage = SetupStage.Intro; return; }

        try
        {
            _preview.Stop();
            _engine.Start(SelectedMic.Id, SelectedApp.RootPid, _cable.PlaybackId);
            IsOnAir = true;
            InfoMessage = null;
            _settings.MicDeviceId = SelectedMic.Id;
            _settings.LastAppExe = SelectedApp.ExeName;
            _settings.Save();
            ApplyHeadsetVolume();
        }
        catch (Exception ex)
        {
            IsOnAir = false;
            ErrorMessage = ProcessLoopbackCapture.IsSupported
                ? "Could not start: " + ex.Message
                : "Wardogs Radio needs Windows 10 (May 2020 update) or newer.";
            StartPreview();
        }
    }

    private void StopRadio()
    {
        _engine.Stop();
        IsOnAir = false;
        InfoMessage = null;
        StartPreview();
    }

    private void StartPreview()
    {
        if (SelectedMic != null && !IsOnAir) _preview.Start(SelectedMic.Id);
    }

    partial void OnSelectedMicChanged(MicDevice? value)
    {
        OnPropertyChanged(nameof(CanStart));
        if (value == null) return;
        _settings.MicDeviceId = value.Id;
        _settings.Save();
        if (IsOnAir && SelectedApp != null) StartRadio(); // hot-swap the mic
        else StartPreview();
    }

    partial void OnSelectedAppChanging(AudioApp? oldValue, AudioApp? newValue)
    {
        // Give the previous app its normal Windows volume back.
        if (oldValue != null && newValue?.RootPid != oldValue.RootPid && HeadsetVolume < 100)
            RestoreSessionVolume(oldValue.RootPid);
    }

    partial void OnSelectedAppChanged(AudioApp? value)
    {
        OnPropertyChanged(nameof(CanStart));
        if (value == null) return;
        _settings.LastAppExe = value.ExeName;
        _settings.Save();
        if (IsOnAir && SelectedMic != null) StartRadio(); // hot-swap the app
        ApplyHeadsetVolume();
    }

    partial void OnMicVolumeChanged(double value)
    {
        _engine.MicGain = (float)(value / 100);
        _settings.MicGain = _engine.MicGain;
        _settings.Save();
    }

    partial void OnMusicVolumeChanged(double value)
    {
        _engine.AppGain = (float)(value / 100);
        _settings.AppGain = _engine.AppGain;
        _settings.Save();
    }

    partial void OnHeadsetVolumeChanged(double value)
    {
        _engine.LocalVolume = (float)(value / 100);
        _settings.HeadsetVolume = _engine.LocalVolume;
        _settings.Save();
        ApplyHeadsetVolume();
    }

    private void ApplyHeadsetVolume()
    {
        var app = SelectedApp;
        if (app == null) return;
        var vol = (float)(HeadsetVolume / 100);
        Task.Run(() => { try { AudioApps.SetSessionVolume(app.RootPid, vol); } catch { } });
    }

    private static void RestoreSessionVolume(uint rootPid)
    {
        Task.Run(() => { try { AudioApps.SetSessionVolume(rootPid, 1f); } catch { } });
    }

    // ---- options ----------------------------------------------------------------------------

    partial void OnPushToTalkChanged(bool value)
    {
        _settings.PushToTalk = value;
        _settings.Save();
        if (!value) { _engine.MicOpen = true; MicIsLive = true; }
    }

    partial void OnPushToTalkKeyChanged(int value)
    {
        _settings.PushToTalkKey = value;
        _settings.Save();
        OnPropertyChanged(nameof(PushToTalkKeyName));
    }

    partial void OnIsCapturingKeyChanged(bool value) => OnPropertyChanged(nameof(PushToTalkKeyName));

    partial void OnDuckingChanged(bool value)
    {
        _engine.Ducking = value;
        _settings.Ducking = value;
        _settings.Save();
    }

    [RelayCommand]
    private void SetPushToTalkKey()
    {
        // Wait for the mouse click that pressed this button to be released before listening.
        IsCapturingKey = true;
    }

    private void TickKeys()
    {
        if (IsCapturingKey)
        {
            if (Hotkey.IsDown(0x1B)) { IsCapturingKey = false; return; } // Esc cancels
            var vk = Hotkey.FirstPressed();
            if (vk != 0) { PushToTalkKey = vk; IsCapturingKey = false; }
            return;
        }
        if (!PushToTalk) return;
        bool held = Hotkey.IsDown(PushToTalkKey);
        if (held != _pttHeld)
        {
            _pttHeld = held;
            _engine.MicOpen = held;
            MicIsLive = held;
        }
    }

    private void TickMeters()
    {
        MicLevel = Perceptual(IsOnAir ? _engine.MicLevel : _preview.Level);
        AppLevel = Perceptual(IsOnAir ? _engine.AppLevel : 0f);
    }

    private static float Perceptual(float linear) => Math.Clamp(MathF.Sqrt(Math.Max(0, linear)), 0, 1);

    private void RefreshMicrophones()
    {
        var mics = AudioDevices.ListMicrophones();
        var currentId = SelectedMic?.Id ?? _settings.MicDeviceId;
        var previousIds = Microphones.Select(m => m.Id).ToList();
        if (previousIds.SequenceEqual(mics.Select(m => m.Id))) return;

        Microphones.Clear();
        foreach (var m in mics) Microphones.Add(m);
        SelectedMic = Microphones.FirstOrDefault(m => m.Id == currentId)
                      ?? Microphones.FirstOrDefault(m => m.IsDefault)
                      ?? Microphones.FirstOrDefault();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || IsSetupMode) return;
        _refreshing = true;
        try
        {
            if (++_ticks % 3 == 0) RefreshMicrophones();
            if (NeedsBranding && _ticks % 5 == 0) RecheckCable();

            var fresh = await Task.Run(AudioApps.Enumerate);
            MergeApps(fresh);

            // Remember-the-app behaviour: if the app they used last time shows up, select it.
            if (SelectedApp == null && _settings.LastAppExe != null)
                SelectedApp = Apps.FirstOrDefault(a => a.ExeName.Equals(_settings.LastAppExe, StringComparison.OrdinalIgnoreCase));

            if (_autoStartArmed && !IsOnAir && CanStart)
            {
                _autoStartArmed = false;
                StartRadio();
            }

            // The app was closed and reopened (new PID) while on air: re-attach.
            if (IsOnAir && SelectedApp != null && !fresh.Any(a => a.RootPid == SelectedApp.RootPid))
            {
                var reborn = fresh.FirstOrDefault(a => a.ExeName.Equals(SelectedApp.ExeName, StringComparison.OrdinalIgnoreCase));
                if (reborn != null) SelectedApp = Apps.First(a => a.RootPid == reborn.RootPid);
                else InfoMessage = SelectedApp.DisplayName + " was closed. Your mic is still on the radio; open it again and it reconnects.";
            }

            // Apps open new audio sessions over time; keep the headset level applied to all of them.
            if (SelectedApp != null && HeadsetVolume < 100 && _ticks % 2 == 0) ApplyHeadsetVolume();
        }
        catch { }
        finally { _refreshing = false; }
    }

    private void MergeApps(List<AudioApp> fresh)
    {
        var keepPid = SelectedApp?.RootPid;
        var keepExe = SelectedApp?.ExeName;

        for (int i = Apps.Count - 1; i >= 0; i--)
        {
            var existing = Apps[i];
            var match = fresh.FirstOrDefault(a => a.RootPid == existing.RootPid);
            if (match == null)
            {
                // Keep the selected tile around while on air so the selection does not blink.
                if (IsOnAir && existing.RootPid == keepPid) { existing.IsPlaying = false; continue; }
                Apps.RemoveAt(i);
            }
            else
            {
                existing.IsPlaying = match.IsPlaying;
            }
        }
        foreach (var a in fresh)
            if (!Apps.Any(x => x.RootPid == a.RootPid)) Apps.Add(a);

        if (keepPid != null && SelectedApp == null)
            SelectedApp = Apps.FirstOrDefault(a => a.RootPid == keepPid)
                          ?? Apps.FirstOrDefault(a => a.ExeName.Equals(keepExe, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _meterTimer.Stop();
        _keyTimer.Stop();
        _engine.Dispose();
        _preview.Dispose();
        // Never leave the user's music app quiet in the Windows mixer after we're gone.
        if (SelectedApp != null && HeadsetVolume < 100)
        {
            try { AudioApps.SetSessionVolume(SelectedApp.RootPid, 1f); } catch { }
        }
    }
}
