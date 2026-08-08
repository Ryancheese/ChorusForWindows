using System.Diagnostics;
using ChorusCore.Protocol;

namespace ChorusCore.Sync;

/// <summary>
/// Estimates speaker clock offset relative to host using NTP-style exchange.
/// <c>offset = speakerTime - hostTime</c>  ⇒  <c>hostTime ≈ speakerTime - offset</c>.
/// </summary>
public readonly record struct ClockOffsetEstimate(double Offset, double RoundTrip, double UpdatedAt)
{
    /// <summary>Convert a host timeline instant into speaker local time.</summary>
    public double SpeakerTimeForHostTime(double hostTime) => hostTime + Offset;
}

/// <summary>
/// Maintains a sliding window of clock-offset samples and exposes the one with the
/// lowest round-trip time (classic NTP heuristic). Thread-safe.
/// </summary>
public sealed class ClockSynchronizer
{
    private readonly object _lock = new();
    private readonly List<ClockOffsetEstimate> _samples = new();
    private readonly int _maxSamples;

    public ClockSynchronizer(int maxSamples = 8)
    {
        _maxSamples = maxSamples;
    }

    /// <summary>Best (lowest-RTT) sample, or null if no samples recorded.</summary>
    public ClockOffsetEstimate? BestEstimate
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count == 0) return null;
                var best = _samples[0];
                foreach (var s in _samples)
                    if (s.RoundTrip < best.RoundTrip) best = s;
                return best;
            }
        }
    }

    /// <summary>
    /// Record a clockPong reply. <paramref name="hostReceiveTime"/> is the host monotonic
    /// time when the pong arrived back at the host.
    /// </summary>
    public void RecordPong(ClockPongData pong, double hostReceiveTime)
    {
        var rtt = hostReceiveTime - pong.HostSendTime;
        if (rtt < 0 || rtt >= 2.0) return;
        // Speaker midpoint of receive/send approximates remote processing time.
        double speakerMid = (pong.SpeakerReceiveTime + pong.SpeakerSendTime) / 2.0;
        double hostMid = (pong.HostSendTime + hostReceiveTime) / 2.0;
        double offset = speakerMid - hostMid;
        var estimate = new ClockOffsetEstimate(offset, rtt, hostReceiveTime);

        lock (_lock)
        {
            _samples.Add(estimate);
            if (_samples.Count > _maxSamples)
                _samples.RemoveRange(0, _samples.Count - _maxSamples);
        }
    }

    public void Reset()
    {
        lock (_lock) _samples.Clear();
    }
}

/// <summary>
/// Monotonic clock source. The Mac version wraps <c>ProcessInfo.systemUptime</c>;
/// here we use <see cref="Stopwatch"/>, a high-resolution monotonic clock available on
/// every .NET platform (Windows / macOS / iOS / Android), so Host and Speaker share the
/// same time-base implementation. Returns seconds as a double.
/// </summary>
public static class HostTime
{
    private static readonly double Frequency = Stopwatch.Frequency;

    /// <summary>Monotonic seconds since the stopwatch epoch (process/system start).</summary>
    public static double Now() => Stopwatch.GetTimestamp() / Frequency;
}
