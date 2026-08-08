using NAudio.Wave;

namespace ChorusAudio.Playback;

/// <summary>
/// Plays captured PCM on the Host's own speakers in parallel with the Speakers, so the
/// person at the computer also hears the audio. Uses a <see cref="BufferedWaveProvider"/>
/// fed from the same sample stream that goes out over TCP — no second capture pass.
/// </summary>
public sealed class LocalAudioPlayer : IDisposable
{
    private readonly WaveFormat _format;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _buffer;
    private volatile bool _running;

    public LocalAudioPlayer(double sampleRate = 44100, int channels = 1)
    {
        _format = WaveFormat.CreateIeeeFloatWaveFormat((int)sampleRate, channels);
    }

    public void Start()
    {
        _buffer = new BufferedWaveProvider(_format)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true,
        };
        _waveOut = new WaveOutEvent { DesiredLatency = 150 };
        _waveOut.Init(_buffer);
        _waveOut.Play();
        _running = true;
    }

    /// <summary>Enqueue a batch of mono Float32 samples for local playback.</summary>
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
        try { _waveOut?.Stop(); } catch { }
        try { _waveOut?.Dispose(); } catch { }
        _waveOut = null;
        _buffer = null;
    }

    public void Dispose() => Stop();
}
