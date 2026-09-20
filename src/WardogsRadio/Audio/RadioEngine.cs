using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WardogsRadio.Audio;

/// <summary>
/// The whole signal chain:
///
///      microphone ─ gate (push to talk) ─┐
///                                        ├─ mix ─ soft limiter ─► virtual cable ─► game hears "Wardogs Radio"
///      app audio ─ level ─ ducking ───────┘
///
/// The app keeps playing through the headset; we only tap a copy of it.
/// </summary>
public sealed class RadioEngine : IDisposable
{
    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private WasapiCapture? _mic;
    private ProcessLoopbackCapture? _app;
    private WasapiOut? _out;
    private VolumeSampleProvider? _micVolume;
    private VolumeSampleProvider? _appVolume;
    private SmoothGain? _micGate;
    private SmoothGain? _duck;
    private readonly object _gate = new();
    private DateTime _lastVoice = DateTime.MinValue;

    public bool IsRunning { get; private set; }
    public float MicLevel { get; private set; }
    public float AppLevel { get; private set; }

    public event EventHandler<string>? Faulted;

    private float _micGain = 1f, _appGain = 0.6f, _localVolume = 1f;
    private bool _micOpen = true, _ducking;

    /// <summary>How loud the voice is on the radio (1 = unity).</summary>
    public float MicGain { get => _micGain; set { _micGain = value; if (_micVolume != null) _micVolume.Volume = value; } }

    /// <summary>How loud the music is on the radio (0..1).</summary>
    public float AppGain { get => _appGain; set { _appGain = value; ApplyAppGain(); } }

    /// <summary>The app's Windows mixer volume (what the user hears). The capture is post-mixer, so we
    /// divide it back out to keep the radio level independent of the headset level.</summary>
    public float LocalVolume { get => _localVolume; set { _localVolume = Math.Clamp(value, 0.02f, 1f); ApplyAppGain(); } }

    /// <summary>False while push-to-talk is enabled and the key is not held.</summary>
    public bool MicOpen { get => _micOpen; set { _micOpen = value; _micGate?.SetTarget(value ? 1f : 0f); } }

    /// <summary>Turn the music down while the pilot is talking.</summary>
    public bool Ducking { get => _ducking; set { _ducking = value; if (!value) _duck?.SetTarget(1f); } }

    private const float DuckLevel = 0.3f;
    private const float VoiceThreshold = 0.04f;
    private static readonly TimeSpan DuckHold = TimeSpan.FromMilliseconds(350);

    private void ApplyAppGain()
    {
        if (_appVolume != null) _appVolume.Volume = _appGain / _localVolume;
    }

    public void Start(string micDeviceId, uint appRootPid, string cablePlaybackId)
    {
        lock (_gate)
        {
            StopCore();
            try
            {
                var micDevice = AudioDevices.GetDevice(micDeviceId)
                    ?? throw new InvalidOperationException("Microphone is no longer connected.");
                var cableDevice = AudioDevices.GetDevice(cablePlaybackId)
                    ?? throw new InvalidOperationException("Virtual microphone not found. Restart Wardogs Radio to reinstall it.");

                // --- microphone -----------------------------------------------------------------
                _mic = new WasapiCapture(micDevice, true, 20);
                var micBuffer = new BufferedWaveProvider(_mic.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(500),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true,
                };
                _mic.DataAvailable += (_, e) => { micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded); TrimIfLagging(micBuffer); };
                _mic.RecordingStopped += (_, e) => { if (e.Exception != null) Fault("Microphone stopped: " + e.Exception.Message); };
                _micVolume = new VolumeSampleProvider(ToStereo48k(micBuffer)) { Volume = _micGain };
                _micGate = new SmoothGain(_micVolume, attackMs: 8, releaseMs: 40) { };
                _micGate.SetTarget(_micOpen ? 1f : 0f, immediate: true);
                var micMeter = new MeteringSampleProvider(_micGate, 480);
                micMeter.StreamVolume += (_, e) => { MicLevel = Max(e.MaxSampleValues); UpdateDucking(); };

                // --- application audio ------------------------------------------------------------
                var appBuffer = new BufferedWaveProvider(ProcessLoopbackCapture.OutputFormat)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(500),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true,
                };
                _app = new ProcessLoopbackCapture(appRootPid);
                _app.DataAvailable += (_, e) => { appBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded); TrimIfLagging(appBuffer); };
                _app.Stopped += (_, ex) => { if (ex != null) Fault("App audio stopped: " + ex.Message); };
                _appVolume = new VolumeSampleProvider(appBuffer.ToSampleProvider());
                ApplyAppGain();
                _duck = new SmoothGain(_appVolume, attackMs: 30, releaseMs: 600);
                _duck.SetTarget(1f, immediate: true);
                var appMeter = new MeteringSampleProvider(_duck, 480);
                appMeter.StreamVolume += (_, e) => AppLevel = Max(e.MaxSampleValues);

                // --- mix and send to the cable ----------------------------------------------------
                var mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };
                mixer.AddMixerInput(micMeter);
                mixer.AddMixerInput(appMeter);
                var limited = new SoftLimiter(mixer);

                _out = new WasapiOut(cableDevice, AudioClientShareMode.Shared, true, 30);
                _out.Init(limited);
                _out.PlaybackStopped += (_, e) => { if (e.Exception != null) Fault("Output stopped: " + e.Exception.Message); };

                _app.Start();
                _mic.StartRecording();
                _out.Play();
                IsRunning = true;
            }
            catch
            {
                StopCore();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_gate) StopCore();
    }

    private void StopCore()
    {
        IsRunning = false;
        try { _out?.Stop(); } catch { }
        try { _mic?.StopRecording(); } catch { }
        try { _app?.Stop(); } catch { }
        _out?.Dispose(); _out = null;
        _mic?.Dispose(); _mic = null;
        _app?.Dispose(); _app = null;
        _micVolume = null; _appVolume = null; _micGate = null; _duck = null;
        MicLevel = 0; AppLevel = 0;
    }

    private void UpdateDucking()
    {
        if (_duck == null || !_ducking) return;
        var now = DateTime.UtcNow;
        if (MicLevel > VoiceThreshold) _lastVoice = now;
        _duck.SetTarget(now - _lastVoice < DuckHold ? DuckLevel : 1f);
    }

    private void Fault(string message) => Faulted?.Invoke(this, message);

    private static float Max(float[] values)
    {
        float m = 0f;
        foreach (var v in values) if (v > m) m = v;
        return m;
    }

    // Clocks of the mic, the app, and the cable all drift a little. If a buffer builds up past
    // ~150 ms we drop the backlog so the radio never lags behind the player's voice.
    private static void TrimIfLagging(BufferedWaveProvider buffer)
    {
        if (buffer.BufferedDuration > TimeSpan.FromMilliseconds(150)) buffer.ClearBuffer();
    }

    private static ISampleProvider ToStereo48k(IWaveProvider source)
    {
        ISampleProvider s = source.ToSampleProvider();
        if (s.WaveFormat.Channels == 1) s = new MonoToStereoSampleProvider(s);
        else if (s.WaveFormat.Channels > 2) s = new MultiChannelToStereo(s);
        if (s.WaveFormat.SampleRate != 48000) s = new WdlResamplingSampleProvider(s, 48000);
        return s;
    }

    public void Dispose() => Stop();

    /// <summary>A gain that glides to its target instead of jumping, so gating and ducking never click.</summary>
    private sealed class SmoothGain : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly float _attack, _release;
        private volatile float _target = 1f;
        private float _current = 1f;

        public SmoothGain(ISampleProvider src, double attackMs, double releaseMs)
        {
            _src = src;
            int rate = src.WaveFormat.SampleRate;
            _attack = (float)(1.0 / (attackMs / 1000.0 * rate));
            _release = (float)(1.0 / (releaseMs / 1000.0 * rate));
        }

        public WaveFormat WaveFormat => _src.WaveFormat;

        public void SetTarget(float target, bool immediate = false)
        {
            _target = target;
            if (immediate) _current = target;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = _src.Read(buffer, offset, count);
            int ch = WaveFormat.Channels;
            float target = _target, g = _current;
            for (int i = offset; i < offset + n; i += ch)
            {
                if (g < target) g = Math.Min(target, g + _attack);
                else if (g > target) g = Math.Max(target, g - _release);
                for (int c = 0; c < ch; c++) buffer[i + c] *= g;
            }
            _current = g;
            return n;
        }
    }

    /// <summary>Gentle saturation so mic + music never hard-clips into the game.</summary>
    private sealed class SoftLimiter(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            int n = source.Read(buffer, offset, count);
            for (int i = offset; i < offset + n; i++)
            {
                float x = buffer[i];
                if (x > 0.8f || x < -0.8f) buffer[i] = MathF.Tanh(x);
            }
            return n;
        }
    }

    /// <summary>Folds any channel count down to stereo by averaging odd/even channels.</summary>
    private sealed class MultiChannelToStereo : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private float[] _tmp = Array.Empty<float>();
        public MultiChannelToStereo(ISampleProvider src)
        {
            _src = src;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            int ch = _src.WaveFormat.Channels;
            int frames = count / 2;
            int need = frames * ch;
            if (_tmp.Length < need) _tmp = new float[need];
            int got = _src.Read(_tmp, 0, need) / ch;
            int lCount = (ch + 1) / 2, rCount = Math.Max(1, ch / 2);
            for (int f = 0; f < got; f++)
            {
                float l = 0, r = 0;
                for (int c = 0; c < ch; c++) { if (c % 2 == 0) l += _tmp[f * ch + c]; else r += _tmp[f * ch + c]; }
                buffer[offset + f * 2] = l / lCount;
                buffer[offset + f * 2 + 1] = r / rCount;
            }
            return got * 2;
        }
    }
}
