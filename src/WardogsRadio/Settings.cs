using System.IO;
using System.Text.Json;

namespace WardogsRadio;

public sealed class Settings
{
    public string? MicDeviceId { get; set; }
    public string? LastAppExe { get; set; }
    public float MicGain { get; set; } = 1.0f;
    public float AppGain { get; set; } = 0.6f;
    public bool AutoStart { get; set; } = true;

    // Snapshot of the user's default devices taken before VB-CABLE installs itself as default.
    public string? SavedDefaultPlayback { get; set; }
    public string? SavedDefaultPlaybackComm { get; set; }
    public string? SavedDefaultCapture { get; set; }
    public string? SavedDefaultCaptureComm { get; set; }
    public bool PendingDefaultsRestore { get; set; }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WardogsRadio");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}
