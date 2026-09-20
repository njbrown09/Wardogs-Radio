using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WardogsRadio.Audio;

/// <summary>Cheap level meter for the selected microphone while the radio is off, so people can
/// see their mic works before they hit Start.</summary>
public sealed class MicPreview : IDisposable
{
    private WasapiCapture? _capture;
    public float Level { get; private set; }

    public void Start(string deviceId)
    {
        Stop();
        var device = AudioDevices.GetDevice(deviceId);
        if (device == null) return;
        try
        {
            _capture = new WasapiCapture(device, true, 50);
            var format = _capture.WaveFormat;
            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                           || (format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            _capture.DataAvailable += (_, e) =>
            {
                float peak = 0f;
                if (isFloat)
                {
                    for (int i = 0; i + 4 <= e.BytesRecorded; i += 4)
                    {
                        float v = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
                        if (v > peak) peak = v;
                    }
                }
                else if (format.BitsPerSample == 16)
                {
                    for (int i = 0; i + 2 <= e.BytesRecorded; i += 2)
                    {
                        float v = Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f);
                        if (v > peak) peak = v;
                    }
                }
                Level = peak;
            };
            _capture.StartRecording();
        }
        catch
        {
            Stop();
        }
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
        _capture = null;
        Level = 0;
    }

    public void Dispose() => Stop();
}
