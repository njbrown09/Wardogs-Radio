using System.Runtime.InteropServices;

namespace WardogsRadio.Interop;

// Minimal hand-written Core Audio COM interop for the two things NAudio does not expose:
//  1. Writing an endpoint's display name (needs STGM_READWRITE on the property store, admin only).
//  2. Setting the default playback/recording device (undocumented but long-stable IPolicyConfig).

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FmtId;
    public int PropId;
    public PropertyKey(Guid fmtId, int propId) { FmtId = fmtId; PropId = propId; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantNative
{
    public ushort Vt;
    public ushort Reserved1, Reserved2, Reserved3;
    public IntPtr Pointer;
    public IntPtr Pointer2;

    public const ushort VT_LPWSTR = 31;

    public static PropVariantNative FromString(string value) => new()
    {
        Vt = VT_LPWSTR,
        Pointer = Marshal.StringToCoTaskMemUni(value),
    };

    public string? AsString() => Vt == VT_LPWSTR ? Marshal.PtrToStringUni(Pointer) : null;

    public void Clear() => PropVariantClear(ref this);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariantNative pvar);
}

internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }
internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorCom { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumeratorNative
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDeviceNative device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDeviceNative device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceNative
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStoreNative properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStoreNative
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariantNative value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariantNative value);
    [PreserveSig] int Commit();
}

// IPolicyConfig: undocumented, but the same vtable has shipped unchanged since Windows 7 and is
// what every "audio switcher" utility relies on.
[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClientCom { }

[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, out IntPtr format);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, out long defaultPeriod, out long minPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int bFxStore, ref PropertyKey key, out PropVariantNative value);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int bFxStore, ref PropertyKey key, ref PropVariantNative value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}

internal static class CoreAudioKeys
{
    // PKEY_Device_DeviceDesc: the part of the name the user sees before the "(Interface)" suffix.
    public static readonly PropertyKey DeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    // PKEY_Device_FriendlyName: full composed name, e.g. "CABLE Output (VB-Audio Virtual Cable)".
    public static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    // PKEY_DeviceInterface_FriendlyName: the driver/adapter name, e.g. "VB-Audio Virtual Cable".
    public static readonly PropertyKey InterfaceFriendlyName = new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 6);
}

internal static class EndpointNaming
{
    private const int STGM_READWRITE = 0x2;

    /// <summary>Renames an audio endpoint as it appears in Windows and in every app's device picker.
    /// Requires administrator rights.</summary>
    public static void Rename(string endpointId, string newName)
    {
        var enumerator = (IMMDeviceEnumeratorNative)new MMDeviceEnumeratorCom();
        IMMDeviceNative? device = null;
        IPropertyStoreNative? store = null;
        var value = PropVariantNative.FromString(newName);
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpointId, out device));
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(STGM_READWRITE, out store));
            var key = CoreAudioKeys.DeviceDesc;
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally
        {
            value.Clear();
            if (store != null) Marshal.ReleaseComObject(store);
            if (device != null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }
}

internal static class DefaultDevices
{
    public static string? GetDefault(EDataFlow flow, ERole role)
    {
        var enumerator = (IMMDeviceEnumeratorNative)new MMDeviceEnumeratorCom();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0) return null;
            device.GetId(out var id);
            Marshal.ReleaseComObject(device);
            return id;
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    public static void SetDefault(string endpointId, ERole role)
    {
        var client = (IPolicyConfig)new PolicyConfigClientCom();
        try { Marshal.ThrowExceptionForHR(client.SetDefaultEndpoint(endpointId, role)); }
        finally { Marshal.ReleaseComObject(client); }
    }
}
