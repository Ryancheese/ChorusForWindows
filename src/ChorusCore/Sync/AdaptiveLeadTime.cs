using ChorusCore.Protocol;

namespace ChorusCore.Sync;

/// <summary>
/// Selects a conservative start lead from recent round-trip observations.
/// 1.2–1.5 seconds prioritises continuous playback over immediate start.
/// Mirrors the Swift AdaptiveLeadTime.
/// </summary>
public sealed class AdaptiveLeadTime
{
    private readonly List<double> _roundTrips = new();
    private readonly int _maximumSamples = 20;

    public void RecordRoundTrip(double roundTrip)
    {
        if (roundTrip < 0 || roundTrip >= 2.0) return;
        _roundTrips.Add(roundTrip);
        if (_roundTrips.Count > _maximumSamples)
            _roundTrips.RemoveRange(0, _roundTrips.Count - _maximumSamples);
    }

    public double RecommendedLeadTime
    {
        get
        {
            if (_roundTrips.Count == 0) return SyncProtocol.DefaultLeadTime;
            var sorted = _roundTrips.OrderBy(x => x).ToList();
            int p90Index = Math.Min(sorted.Count - 1, (int)Math.Round((sorted.Count - 1) * 0.9));
            double p90 = sorted[p90Index];
            return Math.Min(Math.Max(1.2, 1.0 + (2.0 * p90)), 1.5);
        }
    }

    /// <summary>
    /// Tight lead for Windows system-audio relay. The PC already plays the live mix
    /// with ~0 delay (no BlackHole), so a 1.2s file-style lead makes the phone feel
    /// a full second late. Keep just enough runway for RTT + a small jitter cushion.
    /// </summary>
    public double RecommendedLiveLeadTime
    {
        get
        {
            if (_roundTrips.Count == 0) return 0.35;
            var sorted = _roundTrips.OrderBy(x => x).ToList();
            int p90Index = Math.Min(sorted.Count - 1, (int)Math.Round((sorted.Count - 1) * 0.9));
            double p90 = sorted[p90Index];
            // ~3× RTT + 120ms cushion, clamped to 250–550ms
            return Math.Clamp(0.12 + (3.0 * p90), 0.25, 0.55);
        }
    }
}
