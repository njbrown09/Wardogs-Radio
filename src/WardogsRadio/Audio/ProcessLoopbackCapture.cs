using System.Runtime.InteropServices;
using NAudio.Wave;

namespace WardogsRadio.Audio;

/// <summary>
/// Captures the audio rendered by one process (and its children) using the Windows 10 2004+
/// process-loopback activation of WASAPI. This is the same mechanism Discord and OBS use for
/// "share this app's audio". Output is always 48 kHz stereo IEEE float.
/// </summary>
internal sealed class ProcessLoopbackCapture : IDisposable
{
    public static readonly WaveFormat OutputFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<Exception?>? Stopped;

    private readonly uint _pid;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly ManualResetEventSlim _started = new(false);
    private Exception? _startError;

    public ProcessLoopbackCapture(uint targetProcessId) => _pid = targetProcessId;

    public static bool IsSupported => Environment.OSVersion.Version >= new Version(10, 0, 19041);

    public void Start()
    {
        if (_thread != null) throw new InvalidOperationException("Already started");
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "ProcessLoopbackCapture" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(8));
        if (_startError != null) throw _startError;
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Run()
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        var evt = new AutoResetEvent(false);
        Exception? error = null;
        try
        {
            client = Activate(_pid);

            var format = OutputFormat;
            var fmtPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormat>() + format.ExtraSize);
            try
            {
                Marshal.StructureToPtr(format, fmtPtr, false);
                // 20 ms buffer, event driven, loopback: exactly what Microsoft's ApplicationLoopback sample does.
                var hr = client.Initialize(AUDCLNT_SHAREMODE_SHARED,
                    AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                    200_000, 0, fmtPtr, IntPtr.Zero);
                Marshal.ThrowExceptionForHR(hr);
            }
            finally { Marshal.FreeHGlobal(fmtPtr); }

            Marshal.ThrowExceptionForHR(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()));
            var iid = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref iid, out var svc));
            capture = (IAudioCaptureClient)svc;
            Marshal.ThrowExceptionForHR(client.Start());

            _started.Set();

            var frameBytes = format.BlockAlign;
            byte[] managed = new byte[frameBytes * 4800];
            while (!_stop)
            {
                // The event only fires while the target is rendering; wake periodically so Stop() is prompt.
                evt.WaitOne(50);
                while (!_stop)
                {
                    Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out var packetFrames));
                    if (packetFrames == 0) break;
                    Marshal.ThrowExceptionForHR(capture.GetBuffer(out var data, out var frames, out var flags, out _, out _));
                    try
                    {
                        int bytes = (int)frames * frameBytes;
                        if (bytes > managed.Length) managed = new byte[bytes];
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(managed, 0, bytes);
                        else Marshal.Copy(data, managed, 0, bytes);
                        DataAvailable?.Invoke(this, new WaveInEventArgs(managed, bytes));
                    }
                    finally { capture.ReleaseBuffer(frames); }
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
            _startError = ex;
            _started.Set();
        }
        finally
        {
            try { client?.Stop(); } catch { }
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            evt.Dispose();
            Stopped?.Invoke(this, error);
        }
    }

    // ---- activation -------------------------------------------------------------------------

    private static IAudioClient Activate(uint pid)
    {
        var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            TargetProcessId = pid,
            ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
        };
        int size = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(size);
        IntPtr propVarPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var pv = new PROPVARIANT_BLOB { vt = VT_BLOB, cbSize = (uint)size, pBlobData = paramsPtr };
            Marshal.StructureToPtr(pv, propVarPtr, false);

            var handler = new CompletionHandler();
            var iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                ref iid, propVarPtr, handler, out var op));
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Windows did not respond to the audio capture request.");
            Marshal.ThrowExceptionForHR(op.GetActivateResult(out var hr, out var unk));
            Marshal.ThrowExceptionForHR(hr);
            Marshal.ReleaseComObject(op);
            return (IAudioClient)unk;
        }
        finally
        {
            Marshal.FreeHGlobal(propVarPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";
    private const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    private const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    private const ushort VT_BLOB = 0x41;
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT_BLOB
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    // IAgileObject lets the audio service call the handler from its own thread without apartment marshalling.
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) { Done.Set(); return 0; }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
