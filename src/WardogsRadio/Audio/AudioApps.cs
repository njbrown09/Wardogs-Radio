using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using WardogsRadio.Interop;

namespace WardogsRadio.Audio;

/// <summary>An application that currently has an audio session open (i.e. could play sound).</summary>
public sealed class AudioApp : INotifyPropertyChanged
{
    private bool _isPlaying;

    public required uint RootPid { get; init; }
    public required string ExeName { get; init; }
    public required string DisplayName { get; init; }
    public BitmapSource? Icon { get; init; }
    public float Peak { get; set; }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => DisplayName;
}

public static class AudioApps
{
    private static readonly Dictionary<string, BitmapSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> HiddenExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "WardogsRadio.exe", "explorer.exe", "ShellExperienceHost.exe", "SearchHost.exe",
        "svchost.exe", "audiodg.exe", "RuntimeBroker.exe", "SystemSettings.exe", "TextInputHost.exe",
        "StartMenuExperienceHost.exe", "LockApp.exe", "NVIDIA Broadcast.exe", "SteelSeriesSonar.exe",
        "nvcontainer.exe", "NVDisplay.Container.exe", "voicemeeter8x64.exe", "voicemeeterpro.exe", "voicemeeter.exe",
        "msedgewebview2.exe", "WidgetService.exe", "Widgets.exe",
        // The game itself. Nobody wants to put the game on the radio, and it is a trap for a stray click.
        "WardogsClient-Win64-Shipping.exe", "Wardogs.exe", "WARDOGS.exe",
    };

    /// <summary>Enumerates every app with an audio session on any active playback device.</summary>
    public static List<AudioApp> Enumerate()
    {
        var result = new Dictionary<uint, AudioApp>();
        var tree = ProcessTree.Snapshot();
        var ownPid = (uint)Environment.ProcessId;

        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            SessionCollection sessions;
            try
            {
                var mgr = device.AudioSessionManager;
                mgr.RefreshSessions();
                sessions = mgr.Sessions;
            }
            catch { continue; }

            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl s;
                try { s = sessions[i]; } catch { continue; }
                try
                {
                    if (s.IsSystemSoundsSession) continue;
                    if (s.State == AudioSessionState.AudioSessionStateExpired) continue;
                    var pid = s.GetProcessID;
                    if (pid == 0 || pid == ownPid) continue;
                    if (!tree.TryGetValue(pid, out var entry)) continue;
                    if (HiddenExes.Contains(entry.ExeName)) continue;

                    var root = ProcessTree.RootOfSameExe(pid, tree);
                    float peak = 0f;
                    try { peak = s.AudioMeterInformation.MasterPeakValue; } catch { }
                    bool active = s.State == AudioSessionState.AudioSessionStateActive;

                    if (result.TryGetValue(root, out var existing))
                    {
                        existing.IsPlaying |= active && peak > 0.001f;
                        existing.Peak = Math.Max(existing.Peak, peak);
                        continue;
                    }

                    var (name, icon) = Describe(root, entry.ExeName);
                    result[root] = new AudioApp
                    {
                        RootPid = root,
                        ExeName = entry.ExeName,
                        DisplayName = name,
                        Icon = icon,
                        IsPlaying = active && peak > 0.001f,
                        Peak = peak,
                    };
                }
                catch { /* sessions come and go while we enumerate; skip anything that vanished */ }
            }
        }

        return result.Values
            .OrderByDescending(a => a.IsPlaying)
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Sets the Windows volume-mixer level of every audio session belonging to the app
    /// (root process and its children). This is what the user hears in their own headset.</summary>
    public static void SetSessionVolume(uint rootPid, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        var tree = ProcessTree.Snapshot();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            SessionCollection sessions;
            try { var mgr = device.AudioSessionManager; mgr.RefreshSessions(); sessions = mgr.Sessions; }
            catch { continue; }
            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var s = sessions[i];
                    var pid = s.GetProcessID;
                    if (pid == 0 || s.State == AudioSessionState.AudioSessionStateExpired) continue;
                    if (pid != rootPid && ProcessTree.RootOfSameExe(pid, tree) != rootPid) continue;
                    if (Math.Abs(s.SimpleAudioVolume.Volume - volume) > 0.005f) s.SimpleAudioVolume.Volume = volume;
                }
                catch { }
            }
        }
    }

    private static (string name, BitmapSource? icon) Describe(uint pid, string exeName)
    {
        string? path = null;
        string baseName = Path.GetFileNameWithoutExtension(exeName);
        string name = baseName;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            try { path = p.MainModule?.FileName; } catch { }
            if (path != null)
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var candidate = info.FileDescription;
                if (string.IsNullOrWhiteSpace(candidate)) candidate = info.ProductName;
                if (!string.IsNullOrWhiteSpace(candidate)) name = candidate.Trim();
            }
        }
        catch { }

        if (name.Length > 40) name = name[..40] + "…";

        BitmapSource? icon = null;
        if (path != null)
        {
            lock (IconCache)
            {
                if (!IconCache.TryGetValue(path, out icon))
                {
                    icon = LoadIcon(path);
                    IconCache[path] = icon;
                }
            }
        }
        return (name, icon);
    }

    private static BitmapSource? LoadIcon(string path)
    {
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico == null) return null;
            using var bmp = ico.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }
}
