// Ported from JotCore/Sources/AudioEngine/AudioLevelCurve.swift.
// The one definition of Athena's 0…1 mic level, and its inverse.
//
// level = min(1, pow(min(rms * 11, 1), 0.65)) — a compressive curve so quiet
// speech lands mid-range instead of hugging the floor and loud speech saturates
// gracefully. It lives here, alone and tested, because every "did they speak?"
// decision in the pipeline is arithmetic on its output.

namespace Athena.Core;

public static class AudioLevelCurve
{
    public const float Gain = 11f;
    public const float Exponent = 0.65f;

    /// <summary>The bottom of the scale — quieter than any real microphone, used so a
    /// digital-silence buffer has a finite dB value instead of −∞.</summary>
    public const double FloorDBFS = -120.0;

    /// <summary>RMS (0…1 linear) → Athena level (0…1).</summary>
    public static float LevelFromRms(float rms)
    {
        if (rms <= 0) return 0;
        return MathF.Min(1f, MathF.Pow(MathF.Min(rms * Gain, 1f), Exponent));
    }

    /// <summary>Athena level → RMS. Exact inverse below saturation; at level 1.0 it
    /// returns the saturation RMS, which is a floor on the true value, not the value.</summary>
    public static float RmsFromLevel(float level)
    {
        if (level <= 0) return 0;
        return MathF.Pow(MathF.Min(level, 1f), 1f / Exponent) / Gain;
    }

    /// <summary>Athena level → dBFS. This is the space the noise-floor estimator works in:
    /// dB is where "6 dB above the room" is a meaningful sentence and
    /// "0.02 above the room" is not.</summary>
    public static double DBFSFromLevel(float level)
    {
        var linear = RmsFromLevel(level);
        if (linear <= 0) return FloorDBFS;
        return Math.Max(FloorDBFS, 20.0 * Math.Log10(linear));
    }
}
