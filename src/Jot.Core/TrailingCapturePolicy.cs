// Ported from JotCore's trailing-capture constants (DictationCoordinator.swift).
// Releasing the key a beat before the last word is finished is a NORMAL human
// gesture — the hand anticipates the mouth. When the user is still speaking at
// key-up, capture continues until they actually stop.

namespace Jot.Core;

public static class TrailingCapturePolicy
{
    /// <summary>Absolute level that reads as speech (≈ −55 dBFS).</summary>
    public const float SpeechThreshold = 0.08f;
    /// <summary>Quiet this long ⇒ they finished the word.</summary>
    public const double QuietToStopSeconds = 0.25;
    /// <summary>Hard cap so a noisy room can never hold a session open.</summary>
    public const double CapSeconds = 1.5;
    /// <summary>How far above the measured room a level must sit to still read
    /// as speech. Only ever RAISES the bar, never lowers it.</summary>
    public const double FloorMarginDB = 3;
    /// <summary>The session must have shown at least this much separation between
    /// speech and room before energy readings are trusted enough to stop early.
    /// Below it: run to the cap and never clip a word.</summary>
    public const double TrustSNR = 12;
    /// <summary>A mis-estimated floor must never make ordinary speech read as quiet.</summary>
    public const float RelativeCap = 0.30f;
    /// <summary>Nothing rose this far above the room ⇒ nobody spoke, whatever the
    /// absolute peak says (empty-transcript honesty).</summary>
    public const double EmptyTranscriptSNRThreshold = 8;
    /// <summary>A discard needs BOTH a quiet absolute peak and no separation from
    /// the room. Can only ever PREVENT a discard, never cause one.</summary>
    public const double DiscardSNRThreshold = 6;

    /// <summary>The level that "still speaking" means for THIS session. Without a
    /// trustworthy room estimate it is the absolute constant (today's behaviour);
    /// with one, the bar is the floor + margin — clamped so it can only rise
    /// above the absolute constant, never fall below it.</summary>
    public static float ThresholdForSession(NoiseFloorEstimator noise)
    {
        if (noise.FloorDB is not { } floorDB || noise.MeasuredSNR is not { } snr || snr < TrustSNR)
            return SpeechThreshold;
        var targetDB = floorDB + FloorMarginDB;
        var relative = AudioLevelCurve.LevelFromRms(MathF.Pow(10f, (float)(targetDB / 20)));
        return MathF.Min(RelativeCap, MathF.Max(SpeechThreshold, relative));
    }
}
