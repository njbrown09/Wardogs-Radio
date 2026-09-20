using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Principal;
using Microsoft.Win32;
using WardogsRadio.Audio;
using WardogsRadio.Interop;

namespace WardogsRadio.Setup;

/// <summary>
/// One-time setup of the virtual microphone. Wardogs Radio has no kernel driver of its own; it uses
/// VB-CABLE (https://vb-audio.com/Cable/), which is donationware. VB-Audio's license allows the
/// package to be copied and distributed as-is, but not folded into another installer without their
/// agreement. So by default we download the official package and run THEIR setup program in front
/// of the user. If you obtain written permission from VB-Audio, flip <see cref="SilentInstall"/>.
/// </summary>
public static class VbCableInstaller
{
    /// <summary>Set to true only with VB-Audio's agreement. Runs "VBCABLE_Setup_x64.exe -i -h".</summary>
    public const bool SilentInstall = false;

    public const string PackageUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
    public const string HomePage = "https://vb-audio.com/Cable/";

    private static string PackageFolder => Path.Combine(Settings.Folder, "vbcable");
    private static string SetupExe => Path.Combine(PackageFolder, "VBCABLE_Setup_x64.exe");
    private static string ReadmeFile => Path.Combine(PackageFolder, "readme.txt");

    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Downloads and unpacks the official VB-CABLE package into %LOCALAPPDATA%\WardogsRadio\vbcable.</summary>
    public static async Task DownloadAsync(IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(PackageFolder);
        if (File.Exists(SetupExe) && File.Exists(ReadmeFile)) { progress.Report(1); return; }

        var zipPath = Path.Combine(PackageFolder, "VBCABLE_Driver_Pack45.zip");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WardogsRadio/1.0");
            using var response = await http.GetAsync(PackageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1L;
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buf = new byte[81920];
            long read = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress.Report(Math.Min(0.95, (double)read / total));
            }
        }
        ZipFile.ExtractToDirectory(zipPath, PackageFolder, overwriteFiles: true);
        File.Delete(zipPath);
        if (!File.Exists(SetupExe)) throw new FileNotFoundException("The VB-CABLE package did not contain the installer.");
        progress.Report(1);
    }

    /// <summary>Remembers the user's current default devices so they can be put back after the
    /// cable installs itself as the default (which it does).</summary>
    public static void SnapshotDefaults(Settings settings)
    {
        settings.SavedDefaultPlayback = DefaultDevices.GetDefault(EDataFlow.Render, ERole.Multimedia);
        settings.SavedDefaultPlaybackComm = DefaultDevices.GetDefault(EDataFlow.Render, ERole.Communications);
        settings.SavedDefaultCapture = DefaultDevices.GetDefault(EDataFlow.Capture, ERole.Multimedia);
        settings.SavedDefaultCaptureComm = DefaultDevices.GetDefault(EDataFlow.Capture, ERole.Communications);
        settings.PendingDefaultsRestore = true;
        settings.Save();
    }

    /// <summary>Puts the defaults back if the cable hijacked them. Safe to call repeatedly.</summary>
    public static void RestoreDefaultsIfHijacked(Settings settings)
    {
        if (!settings.PendingDefaultsRestore) return;
        var cable = AudioDevices.FindCable();
        if (cable == null) return; // not installed yet; nothing to undo
        try
        {
            Restore(EDataFlow.Render, ERole.Console, settings.SavedDefaultPlayback, cable.PlaybackId);
            Restore(EDataFlow.Render, ERole.Multimedia, settings.SavedDefaultPlayback, cable.PlaybackId);
            Restore(EDataFlow.Render, ERole.Communications, settings.SavedDefaultPlaybackComm ?? settings.SavedDefaultPlayback, cable.PlaybackId);
            Restore(EDataFlow.Capture, ERole.Console, settings.SavedDefaultCapture, cable.CaptureId);
            Restore(EDataFlow.Capture, ERole.Multimedia, settings.SavedDefaultCapture, cable.CaptureId);
            Restore(EDataFlow.Capture, ERole.Communications, settings.SavedDefaultCaptureComm ?? settings.SavedDefaultCapture, cable.CaptureId);
        }
        catch { /* best effort */ }
        settings.PendingDefaultsRestore = false;
        settings.Save();
    }

    private static void Restore(EDataFlow flow, ERole role, string? saved, string cableId)
    {
        if (string.IsNullOrEmpty(saved) || saved == cableId) return;
        var current = DefaultDevices.GetDefault(flow, role);
        if (current == cableId && AudioDevices.GetDevice(saved) != null)
            DefaultDevices.SetDefault(saved, role);
    }

    /// <summary>Launches this same exe elevated to run the driver setup and brand the endpoints.
    /// Returns the elevated process, or null if the user declined the UAC prompt.</summary>
    public static Process? LaunchElevatedSetup()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate own executable.");
        var psi = new ProcessStartInfo(exe, "--elevated-setup")
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        try { return Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return null; } // UAC cancelled
    }

    /// <summary>Runs inside the elevated helper process. Installs the driver, waits for the
    /// endpoints to appear, renames them. Returns an exit code the parent understands.</summary>
    public static int RunElevatedSetup()
    {
        if (!IsAdministrator) return ExitCodes.NotAdmin;

        if (AudioDevices.FindCable() == null)
        {
            if (!File.Exists(SetupExe)) return ExitCodes.PackageMissing;
            var psi = new ProcessStartInfo(SetupExe)
            {
                WorkingDirectory = PackageFolder,
                UseShellExecute = true,
                Arguments = SilentInstall ? "-i -h" : "",
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();
        }

        // On current Windows the endpoints usually appear within a few seconds, no reboot needed.
        CableEndpoints? cable = null;
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline && (cable = AudioDevices.FindCable()) == null)
            Thread.Sleep(1000);

        if (cable == null) return ExitCodes.RebootRequired;
        return BrandEndpoints(cable) ? ExitCodes.Success : ExitCodes.RenameFailed;
    }

    /// <summary>Makes the cable show up as "Wardogs Radio" in every microphone picker. Admin only.</summary>
    public static bool BrandEndpoints(CableEndpoints cable)
    {
        try
        {
            if (!cable.CaptureName.StartsWith(AudioDevices.RadioMicName, StringComparison.OrdinalIgnoreCase))
                EndpointNaming.Rename(cable.CaptureId, AudioDevices.RadioMicName);
            if (!cable.PlaybackName.StartsWith(AudioDevices.RadioMicName, StringComparison.OrdinalIgnoreCase))
                EndpointNaming.Rename(cable.PlaybackId, AudioDevices.RadioSpeakerName);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Restart Windows and reopen Wardogs Radio afterwards.</summary>
    public static void RebootAndRelaunch()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null)
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
                key?.SetValue("WardogsRadio", "\"" + exe + "\"");
            }
        }
        catch { }
        Process.Start(new ProcessStartInfo("shutdown", "/r /t 3 /c \"Finishing Wardogs Radio setup\"")
        {
            UseShellExecute = true,
            CreateNoWindow = true,
        });
    }

    public static class ExitCodes
    {
        public const int Success = 0;
        public const int NotAdmin = 10;
        public const int PackageMissing = 11;
        public const int RebootRequired = 12;
        public const int RenameFailed = 13;
    }
}
