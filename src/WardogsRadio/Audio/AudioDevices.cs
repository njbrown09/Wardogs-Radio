using NAudio.CoreAudioApi;

namespace WardogsRadio.Audio;

public sealed record MicDevice(string Id, string Name, bool IsDefault);

/// <summary>The virtual cable, as seen from both ends. Playback = where the engine writes;
/// Capture = what the game selects as its microphone.</summary>
public sealed record CableEndpoints(string PlaybackId, string PlaybackName, string CaptureId, string CaptureName)
{
    public bool IsBranded => CaptureName.StartsWith(AudioDevices.RadioMicName, StringComparison.OrdinalIgnoreCase);
}

public static class AudioDevices
{
    public const string RadioMicName = "Wardogs Radio";
    public const string RadioSpeakerName = "Wardogs Radio (ignore this one)";
    public const string CableInterfaceName = "VB-Audio Virtual Cable";

    public static IReadOnlyList<MicDevice> ListMicrophones()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try { defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID; } catch { }

        var list = new List<MicDevice>();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            if (IsVirtualCableFamily(d)) continue; // never let someone pick the radio as its own input
            list.Add(new MicDevice(d.ID, d.FriendlyName, d.ID == defaultId));
        }
        return list.OrderByDescending(m => m.IsDefault).ThenBy(m => m.Name).ToList();
    }

    /// <summary>Finds VB-CABLE's plain stereo pair. Returns null when the driver is not installed.</summary>
    public static CableEndpoints? FindCable()
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? playback = null, capture = null;

        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (InterfaceName(d) != CableInterfaceName) continue;
            var desc = Desc(d);
            // Newer packs also ship "CABLE In 16ch"; we want the plain stereo input.
            if (desc.Contains("16ch", StringComparison.OrdinalIgnoreCase)) continue;
            if (desc.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
                desc.StartsWith(RadioMicName, StringComparison.OrdinalIgnoreCase))
            { playback = d; break; }
        }
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            if (InterfaceName(d) != CableInterfaceName) continue;
            var desc = Desc(d);
            if (desc.Contains("16ch", StringComparison.OrdinalIgnoreCase)) continue;
            if (desc.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
                desc.StartsWith(RadioMicName, StringComparison.OrdinalIgnoreCase))
            { capture = d; break; }
        }
        if (playback == null || capture == null) return null;
        return new CableEndpoints(playback.ID, Desc(playback), capture.ID, Desc(capture));
    }

    public static MMDevice? GetDevice(string id)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(id);
        }
        catch { return null; }
    }

    internal static bool IsVirtualCableFamily(MMDevice d)
    {
        var iface = InterfaceName(d);
        return iface == CableInterfaceName || iface.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase);
    }

    internal static string InterfaceName(MMDevice d)
    {
        try
        {
            var p = d.Properties;
            if (p.Contains(PropertyKeys.PKEY_DeviceInterface_FriendlyName))
                return p[PropertyKeys.PKEY_DeviceInterface_FriendlyName].Value as string ?? "";
        }
        catch { }
        return "";
    }

    internal static string Desc(MMDevice d)
    {
        try
        {
            var p = d.Properties;
            if (p.Contains(PropertyKeys.PKEY_Device_DeviceDesc))
                return p[PropertyKeys.PKEY_Device_DeviceDesc].Value as string ?? d.FriendlyName;
        }
        catch { }
        return d.FriendlyName;
    }
}
