using ChorusCore.Protocol;

namespace ChorusCore.Sync;

/// <summary>
/// Holds received PCM briefly so Wi-Fi jitter does not turn into audible gaps.
/// Reorders out-of-order chunks by sequence number and releases a contiguous run
/// once enough audio is buffered to start safely. Mirrors the Swift AudioJitterBuffer.
/// Mid-session joiners anchor to the earliest buffered sequence (not zero).
/// Not thread-safe — consume from a single playback thread.
/// </summary>
public sealed class AudioJitterBuffer
{
    public readonly record struct Chunk(AudioChunkHeader Header, byte[] Pcm);

    private readonly double _targetDuration;
    private readonly double _sampleRate;
    private readonly Dictionary<ulong, Chunk> _pending = new();
    private ulong _nextSequence;
    private long _bufferedSamples;
    private bool _started;

    public AudioJitterBuffer(double sampleRate, double targetDuration = 0.8)
    {
        _sampleRate = sampleRate;
        _targetDuration = targetDuration;
    }

    public void Reset()
    {
        _pending.Clear();
        _nextSequence = 0;
        _bufferedSamples = 0;
        _started = false;
    }

    /// <summary>
    /// Append a chunk. Returns contiguous chunks ready to play once enough audio is
    /// buffered to start safely. Duplicate or stale sequence numbers are ignored.
    /// </summary>
    public List<Chunk> Append(AudioChunkHeader header, byte[] pcm)
    {
        var ready = new List<Chunk>();
        if (_pending.ContainsKey(header.Sequence))
            return ready;
        if (_started && header.Sequence < _nextSequence)
            return ready;

        _pending[header.Sequence] = new Chunk(header, pcm);
        _bufferedSamples += (long)header.SampleCount;

        if (!_started)
        {
            var minSeq = _pending.Count > 0 ? _pending.Keys.Min() : header.Sequence;
            bool lateJoin = minSeq > 0;
            double threshold = lateJoin ? Math.Min(_targetDuration, 0.25) : _targetDuration;
            _started = (_bufferedSamples / _sampleRate) >= threshold;
            if (_started)
                _nextSequence = minSeq;
        }
        if (!_started) return ready;

        while (_pending.TryGetValue(_nextSequence, out var chunk))
        {
            _pending.Remove(_nextSequence);
            _bufferedSamples -= (long)chunk.Header.SampleCount;
            ready.Add(chunk);
            _nextSequence++;
        }
        return ready;
    }
}
