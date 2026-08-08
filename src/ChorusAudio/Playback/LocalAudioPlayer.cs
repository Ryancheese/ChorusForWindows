using ChorusCore.Sync;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ChorusAudio.Playback;

/// <summary>
/// Plays Host PCM on a local render device, aligned to the shared hostPlayAt timeline.
/// Starts the WASAPI engine early by measured output latency so audible output ≈ hostPlayAt
/// (same intent as Mac AVAudioTime scheduling).
/// </summary>
public sealed class LocalAudioPlayer : IDisposable
{
    private readonly WaveFormat _format;
    private MMDevice? _device;
    private WasapiOut? _waveOut;
    private BufferedWaveProvider? _buffer;
    private volatile bool _running;
    private CancellationTokenSource? _startCts;

    /// <summary>Seconds of output path delay applied when scheduling StartAt.</summary>
    public double OutputCompensationSeconds { get; private set; }

    public LocalAudioPlayer(double sampleRate = 44100, int channels = 1)
    {
        _format = WaveFormat.CreateIeeeFloatWaveFormat((int)sampleRate, channels);
    }

    public void Start() => StartAt(HostTime.Now());

    /// <param name="outputDeviceId">Optional render device id.</param>
    /// <param name="manualOffsetSeconds">
    /// Extra delay applied to local start (positive = local later / phone-first fix;
    /// negative = local earlier / PC-late fix). Typical trim ±0.08s.
    /// </param>
    public void StartAt(double hostPlayAtUptime, string? outputDeviceId = null, double manualOffsetSeconds = 0)
    {
        Stop();
        _buffer = new BufferedWaveProvider(_format)
        {
            BufferDuration = TimeSpan.FromSeconds(4),
            DiscardOnBufferOverflow = true,
        };

        // Lower shared-mode latency for tighter PC↔phone alignment.
        const int engineLatencyMs = 25;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = !string.IsNullOrEmpty(outputDeviceId)
                ? enumerator.GetDevice(outputDeviceId)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _waveOut = new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: true, latency: engineLatencyMs);
        }
        catch
        {
            try { _device?.Dispose(); } catch { }
            _device = null;
            _waveOut = new WasapiOut(AudioClientShareMode.Shared, engineLatencyMs);
        }

        _waveOut.Init(_buffer);
        OutputCompensationSeconds = EstimateCompensationSeconds(_device, engineLatencyMs);
        _running = true;

        // engineStart + compensation ≈ audible; +manualOffset delays local relative to phone.
        double engineStartAt = hostPlayAtUptime - OutputCompensationSeconds + manualOffsetSeconds;
        _startCts = new CancellationTokenSource();
        var token = _startCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                double delay = engineStartAt - HostTime.Now();
                if (delay > 0.001)
                    await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                _waveOut?.Play();
            }
            catch (OperationCanceledException) { }
            catch { }
        }, token);
    }

    /// <summary>
    /// Prefer measured engine latency only for wired devices (Mac-like).
    /// Wireless paths often under-report, so keep a modest extra cushion.
    /// </summary>
    private static double EstimateCompensationSeconds(MMDevice? device, int engineLatencyMs)
    {
        double sec = engineLatencyMs / 1000.0;
        try
        {
            if (device != null)
            {
                long stream = device.AudioClient.StreamLatency;
                if (stream > 0)
                    sec += stream / 10_000_000.0;

                // After Init, include half a shared buffer as residual uncertainty.
                try
                {
                    var client = device.AudioClient;
                    if (client.BufferSize > 0 && client.MixFormat != null)
                    {
                        double bufSec = client.BufferSize / (double)client.MixFormat.SampleRate;
                        sec += Math.Min(bufSec * 0.25, 0.02);
                    }
                }
                catch { }

                var name = device.FriendlyName ?? "";
                if (LooksWirelessOrExternal(name))
                    sec += 0.12;
            }
            else
            {
                sec += 0.02;
            }
        }
        catch
        {
            sec += 0.03;
        }

        return Math.Clamp(sec, 0.02, 0.40);
    }

    private static bool LooksWirelessOrExternal(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // Avoid matching "USB" inside unrelated names too aggressively; keep common BT/HDMI tokens.
        ReadOnlySpan<string> hints =
        [
            "bluetooth", "bt ", "airpods", "headset", "hdmi", "displayport", "dongle",
            "wh-", "sony wh", "jbl", "bose", "edifier", "漫步者", "蓝牙", "airplay",
        ];
        foreach (var h in hints)
        {
            if (name.Contains(h, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void Enqueue(ReadOnlySpan<float> samples)
    {
        if (!_running || _buffer == null) return;
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
        _buffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Stop()
    {
        _running = false;
        try { _startCts?.Cancel(); } catch { }
        try { _startCts?.Dispose(); } catch { }
        _startCts = null;
        try { _waveOut?.Stop(); } catch { }
        try { _waveOut?.Dispose(); } catch { }
        _waveOut = null;
        try { _device?.Dispose(); } catch { }
        _device = null;
        _buffer = null;
        OutputCompensationSeconds = 0;
    }

    public void Dispose() => Stop();
}
