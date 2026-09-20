using System.Runtime.InteropServices;
using System.Windows.Input;

namespace WardogsRadio.Interop;

/// <summary>Global key state without hooks: polled with GetAsyncKeyState, which sees keys and
/// mouse buttons even while the game has focus.</summary>
internal static class Hotkey
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static bool IsDown(int vk) => vk > 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Returns the first key or mouse button currently held, excluding left/right mouse
    /// buttons (you need those to click "Set key"). 0 if nothing is held.</summary>
    public static int FirstPressed()
    {
        for (int vk = 3; vk < 256; vk++)
        {
            if (vk == 0x01 || vk == 0x02) continue;
            // Skip generic Shift/Ctrl/Alt (0x10-0x12) in favour of the left/right specific codes.
            if (vk >= 0x10 && vk <= 0x12) continue;
            if (IsDown(vk)) return vk;
        }
        return 0;
    }

    public static string Name(int vk) => vk switch
    {
        0 => "none",
        0x04 => "Middle mouse",
        0x05 => "Mouse 4",
        0x06 => "Mouse 5",
        0x20 => "Space",
        0x14 => "Caps Lock",
        0xA0 => "Left Shift",
        0xA1 => "Right Shift",
        0xA2 => "Left Ctrl",
        0xA3 => "Right Ctrl",
        0xA4 => "Left Alt",
        0xA5 => "Right Alt",
        0xC0 => "` (grave)",
        _ => KeyName(vk),
    };

    private static string KeyName(int vk)
    {
        try
        {
            var key = KeyInterop.KeyFromVirtualKey(vk);
            if (key == Key.None) return "Key " + vk;
            var s = key.ToString();
            if (s.StartsWith("D") && s.Length == 2 && char.IsDigit(s[1])) return s[1..];
            if (s.StartsWith("NumPad")) return "Numpad " + s[6..];
            return s;
        }
        catch { return "Key " + vk; }
    }
}
