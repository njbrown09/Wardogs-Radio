using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WardogsRadio.Interop;

/// <summary>Parent/child process lookup via Toolhelp, so an audio session owned by a helper
/// process (Chrome's audio service, Spotify's render child) can be traced to the app that owns it.</summary>
internal static class ProcessTree
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint TH32CS_SNAPPROCESS = 0x2;

    public readonly record struct Entry(uint Pid, uint ParentPid, string ExeName);

    public static Dictionary<uint, Entry> Snapshot()
    {
        var map = new Dictionary<uint, Entry>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return map;
            do { map[e.th32ProcessID] = new Entry(e.th32ProcessID, e.th32ParentProcessID, e.szExeFile); }
            while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    /// <summary>Walks up from <paramref name="pid"/> while the parent has the same exe name, and
    /// returns the top-most ancestor. Spotify child -> Spotify main; Chrome audio service -> Chrome main.</summary>
    public static uint RootOfSameExe(uint pid, Dictionary<uint, Entry> snapshot)
    {
        if (!snapshot.TryGetValue(pid, out var cur)) return pid;
        var guard = 0;
        while (guard++ < 32 && snapshot.TryGetValue(cur.ParentPid, out var parent)
               && parent.Pid != cur.Pid
               && string.Equals(parent.ExeName, cur.ExeName, StringComparison.OrdinalIgnoreCase)
               && ParentIsOlder(parent.Pid, cur.Pid))
        {
            cur = parent;
        }
        return cur.Pid;
    }

    // PIDs get reused; make sure the "parent" really started before the child.
    private static bool ParentIsOlder(uint parentPid, uint childPid)
    {
        try
        {
            using var p = Process.GetProcessById((int)parentPid);
            using var c = Process.GetProcessById((int)childPid);
            return p.StartTime <= c.StartTime;
        }
        catch { return false; }
    }
}
