using System.Threading;
using ChorusCore.Audio;
using ChorusCore.Protocol;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ChorusAudio.Capture;

/// <summary>
/// Reads a local audio file (mp3/wav/m4a/flac/aac/ogg/wma) and streams its decoded
/// PCM through the <see cref="IAudioCapture"/> callback as mono Float32 at
/// <see cref="SyncProtocol.SampleRate"/>. Raises <see cref="FileEnded"/> when the
/// file is fully consumed so the Host can auto-advance to the next track.
/// </summary>
public sealed class FileAudioCapture : IAudioCapture
{
    private WaveStream? _reader;
    private ISampleProvider? _provider;
    private Thread? _readThread;
    private volatile bool _running;

    public double SampleRate => SyncProtocol.SampleRate;
    public int Channels => SyncProtocol.Channels;
    public string FilePath { get; }
    public string Title { get; }

    /// <summary>Raised on the read thread when the file is fully consumed.</summary>
    public event Action? FileEnded;
    public long SamplesSent;
    public float Peak;

    public FileAudioCapture(string filePath, string title)
    {
        FilePath = filePath;
        Title = title;
    }

    public static bool IsSupported(string filePath) => AudioFormats.IsSupported(filePath);

    public void Start()
    {
        _reader = new MediaFoundationReader(FilePath);
        var fmt = _reader.WaveFormat;

        // Build downmix + resample chain
        ISampleProvider chain = _reader.ToSampleProvider();
        if (chain.WaveFormat.Channels > 1)
            chain = new StereoToMonoSampleProvider(chain) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (Math.Abs(chain.WaveFormat.SampleRate - SyncProtocol.SampleRate) > 0.5)
            chain = new WdlResamplingSampleProvider(chain, (int)SyncProtocol.SampleRate);
        _provider = chain;

        _running = true;
        Peak = 0;
        SamplesSent = 0;
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "chorus-file-read" };
        _readThread.Start();
    }

    private void ReadLoop()
    {
        var buf = new float[4096];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long samplesEmitted = 0;
        while (_running && _provider != null && _reader != null)
        {
            // Check if file is exhausted
            if (_reader.Position >= _reader.Length)
            {
                try { FileEnded?.Invoke(); } catch { }
                break;
            }

            int read = _provider.Read(buf, 0, buf.Length);
            if (read <= 0)
            {
                // End of stream or transient — try a small sleep and retry
                Thread.Sleep(5);
                continue;
            }

            Interlocked.Add(ref SamplesSent, read);
            float peak = 0;
            for (int i = 0; i < read; i++)
            {
                var a = Math.Abs(buf[i]);
                if (a > peak) peak = a;
            }
            if (peak > Peak) Peak = peak;

            var data = new float[read];
            Array.Copy(buf, data, read);
            try { SamplesAvailable?.Invoke(data); } catch { }

            // Pace near realtime so local buffer / send queue don't race ahead of hostPlayAt.
            samplesEmitted += read;
            double idealSec = samplesEmitted / SampleRate;
            double ahead = idealSec - clock.Elapsed.TotalSeconds;
            if (ahead > 0.02)
                Thread.Sleep(TimeSpan.FromSeconds(Math.Min(ahead, 0.2)));
        }
    }

    public void Stop()
    {
        _running = false;
        try { _readThread?.Join(500); } catch { }
        try { _reader?.Dispose(); } catch { }
        _reader = null;
        _provider = null;
    }

    public void Dispose() => Stop();

    public event Action<ReadOnlyMemory<float>>? SamplesAvailable;
    public event Action<string>? ErrorOccurred;
}
