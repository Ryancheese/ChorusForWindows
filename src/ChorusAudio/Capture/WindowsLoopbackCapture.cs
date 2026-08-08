using System.Buffers;
using System.Threading;
using ChorusCore.Audio;
using ChorusCore.Protocol;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ChorusAudio.Capture;

/// <summary>
/// Captures the Windows system mix via WASAPI loopback (shared mode), then
/// downmixes to mono Float32 @ 44100 Hz for Chorus streaming.
/// Processes raw WASAPI buffers inline (avoids BufferedWaveProvider dropouts).
/// </summary>
public sealed class WindowsLoopbackCapture : IAudioCapture
{
    private readonly string? _renderDeviceId;
    private MMDevice? _device;
    private NAudio.Wave.WasapiLoopbackCapture? _capture;
    private volatile bool _running;

    public double SampleRate => SyncProtocol.SampleRate; // 44100
    public int Channels => SyncProtocol.Channels;        // 1

    public long TotalBytesCaptured;
    public float RawPeak;
    public float OutPeak;
    public string CaptureFormat { get; private set; } = "";

    public event Action<ReadOnlyMemory<float>>? SamplesAvailable;
    public event Action<string>? ErrorOccurred;

    private float _lastInputSample;
    private double _resamplePos;

    /// <param name="renderDeviceId">
    /// Optional render endpoint to loop back (e.g. VB-Cable Input). Null = current default.
    /// </param>
    public WindowsLoopbackCapture(string? renderDeviceId = null)
    {
        _renderDeviceId = renderDeviceId;
    }

    public void Start()
    {
        try
        {
            // Keep MMDevice alive for the lifetime of WasapiLoopbackCapture.
            using var enumerator = new MMDeviceEnumerator();
            _device = !string.IsNullOrEmpty(_renderDeviceId)
                ? enumerator.GetDevice(_renderDeviceId)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _capture = new NAudio.Wave.WasapiLoopbackCapture(_device);
            CaptureFormat =
                $"{_capture.WaveFormat.SampleRate / 1000.0:0.#} kHz · {_capture.WaveFormat.Channels} ch · {_capture.WaveFormat.Encoding} · {_device.FriendlyName}";
        }
        catch (Exception ex)
        {
            try { _device?.Dispose(); } catch { }
            _device = null;
            ErrorOccurred?.Invoke($"无法打开系统音频环回：{ex.Message}");
            return;
        }

        _lastInputSample = 0;
        _resamplePos = 0;
        RawPeak = 0;
        OutPeak = 0;
        TotalBytesCaptured = 0;

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _running = true;
        try
        {
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            _running = false;
            ErrorOccurred?.Invoke($"系统音频捕获启动失败：{ex.Message}");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_running || _capture == null || e.BytesRecorded <= 0) return;
        Interlocked.Add(ref TotalBytesCaptured, e.BytesRecorded);

        var fmt = _capture.WaveFormat;
        int channels = Math.Max(1, fmt.Channels);
        bool isFloat = IsIeeeFloat(fmt);
        int bytesPerSample = fmt.BitsPerSample / 8;
        if (bytesPerSample <= 0) bytesPerSample = isFloat ? 4 : 2;

        int frameCount = e.BytesRecorded / (bytesPerSample * channels);
        if (frameCount <= 0) return;

        float[] mono = ArrayPool<float>.Shared.Rent(frameCount);
        try
        {
            float rawPeak = 0;
            if (isFloat && bytesPerSample == 4)
            {
                float downmixScale = 0.8f / channels;
                for (int i = 0; i < frameCount; i++)
                {
                    float sum = 0;
                    int baseIdx = i * channels * 4;
                    for (int c = 0; c < channels; c++)
                        sum += BitConverter.ToSingle(e.Buffer, baseIdx + c * 4);
                    float m = sum * downmixScale;
                    mono[i] = m;
                    float a = Math.Abs(m);
                    if (a > rawPeak) rawPeak = a;
                }
            }
            else if (bytesPerSample == 2)
            {
                float downmixScale = 0.8f / channels / 32768f;
                for (int i = 0; i < frameCount; i++)
                {
                    float sum = 0;
                    int baseIdx = i * channels * 2;
                    for (int c = 0; c < channels; c++)
                        sum += BitConverter.ToInt16(e.Buffer, baseIdx + c * 2);
                    float m = sum * downmixScale;
                    mono[i] = m;
                    float a = Math.Abs(m);
                    if (a > rawPeak) rawPeak = a;
                }
            }
            else if (bytesPerSample == 3)
            {
                // 24-bit PCM packed
                float downmixScale = 0.8f / channels / 8388608f;
                for (int i = 0; i < frameCount; i++)
                {
                    float sum = 0;
                    int baseIdx = i * channels * 3;
                    for (int c = 0; c < channels; c++)
                    {
                        int o = baseIdx + c * 3;
                        int sample = e.Buffer[o] | (e.Buffer[o + 1] << 8) | (e.Buffer[o + 2] << 16);
                        if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
                        sum += sample;
                    }
                    float m = sum * downmixScale;
                    mono[i] = m;
                    float a = Math.Abs(m);
                    if (a > rawPeak) rawPeak = a;
                }
            }
            else
            {
                // Unsupported — emit silence for this buffer rather than crashing the session.
                Array.Clear(mono, 0, frameCount);
            }

            if (rawPeak > RawPeak) RawPeak = rawPeak;

            double inputRate = fmt.SampleRate;
            double outputRate = SyncProtocol.SampleRate;
            if (inputRate <= 0) return;
            double ratio = inputRate / outputRate;

            // Near 1:1 — copy with light rate correction; otherwise linear resample.
            if (Math.Abs(inputRate - outputRate) < 0.5)
            {
                var exact = new float[frameCount];
                Array.Copy(mono, exact, frameCount);
                if (rawPeak > OutPeak) OutPeak = rawPeak;
                SamplesAvailable?.Invoke(exact);
                return;
            }

            int estOutCount = (int)(frameCount / ratio) + 4;
            float[] output = new float[estOutCount];
            int outIdx = 0;
            double endInputPos = frameCount;

            while (_resamplePos < endInputPos - 1e-9)
            {
                int idx = (int)Math.Floor(_resamplePos);
                double frac = _resamplePos - idx;
                float s0 = idx <= 0 ? (idx == 0 ? mono[0] : _lastInputSample) : mono[Math.Min(idx, frameCount - 1)];
                float s1 = mono[Math.Min(idx + 1, frameCount - 1)];
                if (idx < 0) s0 = _lastInputSample;
                float outSample = (float)(s0 + (s1 - s0) * frac);
                if (outIdx < output.Length)
                {
                    output[outIdx++] = outSample;
                    float a = Math.Abs(outSample);
                    if (a > OutPeak) OutPeak = a;
                }
                _resamplePos += ratio;
            }

            _resamplePos -= frameCount;
            _lastInputSample = mono[frameCount - 1];

            if (outIdx > 0)
                SamplesAvailable?.Invoke(output.AsMemory(0, outIdx));
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"系统音频处理失败：{ex.Message}");
        }
        finally
        {
            ArrayPool<float>.Shared.Return(mono);
        }
    }

    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
    private static readonly Guid IeeeFloatSubType = new("00000003-0000-0010-8000-00aa00389b71");

    private static bool IsIeeeFloat(WaveFormat fmt)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        if (fmt is WaveFormatExtensible ext)
            return ext.SubFormat == IeeeFloatSubType;
        // Many WASAPI loopback devices report Extensible + 32-bit float.
        return fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32;
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
        try
        {
            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
            }
        }
        catch { }
        _capture = null;
        try { _device?.Dispose(); } catch { }
        _device = null;
    }

    public void Dispose() => Stop();
}
