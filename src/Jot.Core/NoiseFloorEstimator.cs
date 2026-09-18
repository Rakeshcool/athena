// Ported from JotCore/Sources/SessionCoordinator/NoiseFloorEstimator.swift.
// Measures how loud the room is, so "did they speak?" can be asked relative to
// the room instead of against a constant that assumes a quiet one.
//
// This type always runs, and it decides nothing: it observes the level stream
// and records numbers; whether anything acts on them is the caller's business.

namespace Jot.Core;

public sealed class NoiseFloorEstimator
{
    /// <summary>Below this many samples the percentile is meaningless — at the
    /// ~10 Hz meter rate that's ~0.8s of audio.</summary>
    public const int MinimumSamples = 8;
    /// <summary>The floor is the quietest tenth of the session, not the minimum:
    /// a single anomalous buffer shouldn't define the room.</summary>
    public const double Percentile = 0.10;

    private readonly int _capacity;
    private readonly List<double> _samples;

    public double PeakDB { get; private set; }
    public int SampleCount { get; private set; }

    public NoiseFloorEstimator(int capacity = 600)
    {
        _capacity = Math.Max(MinimumSamples, capacity);
        _samples = new List<double>(_capacity);
        PeakDB = AudioLevelCurve.FloorDBFS;
        SampleCount = 0;
    }

    public void Ingest(float level)
    {
        var db = AudioLevelCurve.DBFSFromLevel(level);
        if (db > PeakDB) PeakDB = db;
        SampleCount++;
        if (_samples.Count == _capacity) _samples.RemoveAt(0);
        _samples.Add(db);
    }

    /// <summary>The room, in dBFS — the 10th percentile of everything heard so far.
    /// A rolling low percentile rather than "the first N milliseconds": audio
    /// starts the instant the key goes down, which is exactly when a fast user is
    /// already speaking, so treating the head as noise would classify speech as
    /// the floor. Speech is intermittent — gaps between words — so the percentile
    /// converges within a second or two even when speech starts at sample 0.</summary>
    public double? FloorDB
    {
        get
        {
            if (_samples.Count < MinimumSamples) return null;
            var sorted = new List<double>(_samples);
            sorted.Sort();
            var index = (int)Math.Round((sorted.Count - 1) * Percentile);
            return sorted[index];
        }
    }

    /// <summary>Peak minus floor. A LOWER BOUND on true SNR (the level curve
    /// saturates, so loud speech understates its own peak) — biasing low means we
    /// conclude "noisy" more readily than "clean", and every consequence of
    /// "noisy" keeps audio rather than dropping it.</summary>
    public double? MeasuredSNR
    {
        get
        {
            var floor = FloorDB;
            return floor is null ? null : PeakDB - floor.Value;
        }
    }
}
