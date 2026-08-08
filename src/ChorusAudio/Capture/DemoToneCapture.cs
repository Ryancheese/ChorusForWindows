using System.Threading;
using ChorusCore.Audio;
using ChorusCore.Protocol;

namespace ChorusAudio.Capture;

/// <summary>
/// Generates a looping A4 (440 Hz) sine tone with a short attack/release envelope,
/// for first-run testing of the sync pipeline without a music file. Mirrors the Swift
/// <c>DemoTone</c>. Delivers mono Float32 @ 44100 Hz in real-time-paced batches.
/// </summary>
public sealed class DemoToneCapture : IAudioCapture
{
    private readonly float[] _tone;
    private int _position;
    private volatile bool _running;
    private Thread? _playThread;

    public double SampleRate => SyncProtocol.SampleRate;
    public int Channels => SyncProtocol.Channels;

    public event Action<ReadOnlyMemory<float>>? SamplesAvailable;
    public event Action<string>? ErrorOccurred;

    public DemoToneCapture(double frequency = 440.0, double duration = 4.0)
    {
        _tone = Generate(frequency, duration);
    }

    private static float[] Generate(double frequency, double duration)
    {
        int count = (int)(duration * SyncProtocol.SampleRate);
        var samples = new float[count];
        double sr = SyncProtocol.SampleRate;
        double twoPiF = 2.0 * Math.PI * frequency;
        double attackSamples = sr * 0.02;   // 20ms attack
        double releaseSamples = sr * 0.05;  // 50ms release
        for (int i = 0; i < count; i++)
        {
            double t = i / sr;
            double attack = Math.Min(1.0, i / attackSamples);
            double release = Math.Min(1.0, (count - i) / releaseSamples);
            double envelope = attack * release;
            samples[i] = (float)(Math.Sin(twoPiF * t) * 0.35 * envelope);
        }
        return samples;
    }

    public void Start()
    {
        _position = 0;
        _running = true;
        _playThread = new Thread(PlayLoop) { IsBackground = true, Name = "chorus-demo-tone" };
        _playThread.Start();
    }

    private void PlayLoop()
    {
        const int chunkSize = 2048;
        var chunk = new float[chunkSize];
        double msPerChunk = chunkSize / SyncProtocol.SampleRate * 1000.0;
        while (_running)
        {
            int written = 0;
            while (written < chunkSize)
            {
                int remaining = chunkSize - written;
                int available = _tone.Length - _position;
                int toCopy = Math.Min(remaining, available);
                Array.Copy(_tone, _position, chunk, written, toCopy);
                written += toCopy;
                _position += toCopy;
                if (_position >= _tone.Length) _position = 0; // loop
            }
            SamplesAvailable?.Invoke(chunk);
            Thread.Sleep((int)msPerChunk);
        }
    }

    public void Stop()
    {
        _running = false;
        _playThread?.Join(500);
    }

    public void Dispose() => Stop();
}
