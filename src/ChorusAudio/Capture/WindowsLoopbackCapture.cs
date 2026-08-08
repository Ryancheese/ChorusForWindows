using System.Buffers;
using System.Threading;
using ChorusCore.Audio;
using ChorusCore.Protocol;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ChorusAudio.Capture;

/// <summary>
/// Captures the Windows system mix via WASAPI loopback (shared mode), then
/// directly downmixes to mono and linearly resamples to 44100 Hz Float32.
/// Bypasses NAudio's BufferedWaveProvider + resampler chain (which intermittently
/// returns zero samples) by processing raw WASAPI buffers inline.
/// </summary>
public sealed class WindowsLoopbackCapture : IAudioCapture
{
    private NAudio.Wave.WasapiLoopbackCapture? _capture;
    private volatile bool _running;

    public double SampleRate => SyncProtocol.SampleRate; // 44100
    public int Channels => SyncProtocol.Channels;        // 1

    /// <summary>Total raw bytes received from WASAPI since Start.</summary>
    public long TotalBytesCaptured;
    /// <summary>Peak of raw WASAPI samples (before downmix/resample). 0 = captured silence.</summary>
    public float RawPeak;
    /// <summary>Peak of processed output samples (after downmix/resample).</summary>
    public float OutPeak;
    /// <summary>Human-readable device format + name.</summary>
    public string CaptureFormat { get; private set; } = "";

    public event Action<ReadOnlyMemory<float>>? SamplesAvailable;
    public event Action<string>? ErrorOccurred;

    // 线性重采样残留：上一次输入的最后一个 mono 样本，用于跨 buffer 插值
    private float _lastInputSample;
    private double _resamplePos; // 输出采样位置在输入流中的浮点位置

    public void Start()
    {
        _capture = new NAudio.Wave.WasapiLoopbackCapture();
        var fmt = _capture.WaveFormat;
        string deviceName = "";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            deviceName = " · " + defaultDevice.FriendlyName;
        }
        catch { }
        CaptureFormat = $"{fmt.SampleRate / 1000.0:0.#} kHz · {fmt.Channels} ch · {fmt.Encoding}{deviceName}";

        _lastInputSample = 0;
        _resamplePos = 0;
        RawPeak = 0;
        OutPeak = 0;
        TotalBytesCaptured = 0;

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _running = true;
        _capture.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_running) return;
        Interlocked.Add(ref TotalBytesCaptured, e.BytesRecorded);

        var fmt = _capture!.WaveFormat;
        int channels = fmt.Channels;
        int bytesPerSample = fmt.BitsPerSample / 8;
        // WASAPI loopback 通常是 32-bit float
        if (bytesPerSample <= 0) bytesPerSample = 4;

        int frameCount = e.BytesRecorded / (bytesPerSample * channels);
        if (frameCount == 0) return;

        // Step 1: 解码 + downmix 到 mono（用 ArrayPool 借还，减少 GC 压力）
        float[] mono = ArrayPool<float>.Shared.Rent(frameCount);
        try
        {
        float rawPeak = 0;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            float downmixScale = 0.8f / channels; // 留 20% 余量防 clipping
            for (int i = 0; i < frameCount; i++)
            {
                float sum = 0;
                int baseIdx = (i * channels) * 4;
                for (int c = 0; c < channels; c++)
                    sum += BitConverter.ToSingle(e.Buffer, baseIdx + c * 4);
                float m = sum * downmixScale;
                mono[i] = m;
                float a = Math.Abs(m);
                if (a > rawPeak) rawPeak = a;
            }
        }
        else
        {
            rawPeak = 0;
        }

        if (rawPeak > RawPeak) RawPeak = rawPeak;

        // Step 2: 线性重采样 inputRate -> 44100（预估输出数量，一次分配，不用 List+ToArray）
        double inputRate = fmt.SampleRate;
        double outputRate = SyncProtocol.SampleRate;
        double ratio = inputRate / outputRate;

        int estOutCount = (int)(frameCount / ratio) + 2;
        float[] output = new float[estOutCount];
        int outIdx = 0;

        double endInputPos = frameCount;
        while (_resamplePos < endInputPos - 1)
        {
            int idx = (int)Math.Floor(_resamplePos);
            double frac = _resamplePos - idx;
            float s0 = idx < 0 ? _lastInputSample : mono[idx];
            float s1 = mono[idx + 1];
            float outSample = (float)(s0 + (s1 - s0) * frac);
            output[outIdx] = outSample;
            float a = Math.Abs(outSample);
            if (a > OutPeak) OutPeak = a;
            _resamplePos += ratio;
            outIdx++;
        }
        _resamplePos -= frameCount;
        _lastInputSample = mono[frameCount - 1];

        if (outIdx > 0)
        {
            SamplesAvailable?.Invoke(output.AsMemory(0, outIdx));
        }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(mono);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            ErrorOccurred?.Invoke(e.Exception.Message);
        _running = false;
    }

    public void Stop()
    {
        _running = false;
        try { _capture?.StopRecording(); } catch { }
    }

    public void Dispose() => Stop();
}
