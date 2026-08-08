namespace ChorusCore.Audio;

/// <summary>
/// Platform-agnostic audio source for the Host. Produces mono Float32 PCM at the
/// target sample rate (44100 Hz to match the wire protocol). Each batch is handed off
/// via <see cref="SamplesAvailable"/> for framing and transmission.
/// Implementations: WASAPI loopback (Windows), demo tone (any platform, for testing).
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Target sample rate (Hz). PCM is delivered already converted to this rate.</summary>
    double SampleRate { get; }

    /// <summary>Target channel count (always 1, mono).</summary>
    int Channels { get; }

    /// <summary>Raised on the capture thread with a batch of mono Float32 samples.</summary>
    event Action<ReadOnlyMemory<float>>? SamplesAvailable;

    /// <summary>Raised if capture fails or the device becomes unavailable.</summary>
    event Action<string>? ErrorOccurred;

    void Start();
    void Stop();
}
