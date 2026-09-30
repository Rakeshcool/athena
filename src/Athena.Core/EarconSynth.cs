// The G-major earcon family (start = D5→G5 rise ~160ms; stop = mirror;
// success = G5 tap; error = muted F♯4+G4 dyad; lock = G4-B4-D5 arpeggio),
// synthesized at startup —
// no sound files to ship, same <400ms, quiet, frame-synced-to-state feel.

using System.IO;

namespace Athena.Core;

public enum Earcon
{
    Start,
    Stop,
    Success,
    Error,
    Lock,
}

public static class EarconSynth
{
    private const int SampleRate = 44100;

    /// <summary>Frequencies for a pleasant, quiet G-major family.</summary>
    private const double G4 = 392.00, B4 = 493.88, D5 = 587.33, G5 = 783.99;
    private const double Fs4 = 369.99;

    public static byte[] Render(Earcon earcon)
    {
        var (notes, totalMs, gain) = earcon switch
        {
            // Start: D5→G5 rise, ~160ms.
            Earcon.Start => (new[] { (D5, 0.0, 0.09), (G5, 0.07, 0.09) }, 180, 0.30),
            // Stop: the mirror fall.
            Earcon.Stop => (new[] { (G5, 0.0, 0.09), (D5, 0.07, 0.09) }, 180, 0.28),
            // Success: a single G5 tap.
            Earcon.Success => (new[] { (G5, 0.0, 0.10) }, 140, 0.30),
            // Error: muted F♯4+G4 dyad (dissonant but soft).
            Earcon.Error => (new[] { (Fs4, 0.0, 0.22), (G4, 0.0, 0.22) }, 260, 0.22),
            // Lock: G4-B4-D5 arpeggio (hands-free confirmation).
            Earcon.Lock => (new[] { (G4, 0.0, 0.07), (B4, 0.06, 0.07), (D5, 0.12, 0.07) }, 240, 0.28),
            _ => (new[] { (G4, 0.0, 0.1) }, 120, 0.2),
        };

        var totalSamples = (int)(totalMs / 1000.0 * SampleRate);
        var samples = new float[totalSamples];
        foreach (var (freq, startSec, durSec) in notes)
        {
            var startIdx = (int)(startSec * SampleRate);
            var dur = (int)(durSec * SampleRate);
            for (var i = 0; i < dur && startIdx + i < totalSamples; i++)
            {
                var t = (double)i / SampleRate;
                // Soft attack/decay envelope; slight sine + 2nd harmonic warmth.
                var env = Math.Min(1.0, i / (0.010 * SampleRate)) *
                          Math.Exp(-3.2 * t / durSec);
                var v = Math.Sin(2 * Math.PI * freq * t)
                        + 0.25 * Math.Sin(4 * Math.PI * freq * t);
                samples[startIdx + i] += (float)(gain * env * v / 1.25);
            }
        }

        return ToWav16(samples, SampleRate);
    }

    private static byte[] ToWav16(float[] samples, int sampleRate)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            var dataLen = samples.Length * 2;
            w.Write("RIFF"u8);
            w.Write(36 + dataLen);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);            // PCM
            w.Write((short)1);            // mono
            w.Write(sampleRate);
            w.Write(sampleRate * 2);      // byte rate
            w.Write((short)2);            // block align
            w.Write((short)16);           // bits
            w.Write("data"u8);
            w.Write(dataLen);
            foreach (var s in samples)
                w.Write((short)Math.Clamp(s, -1f, 1f) * 32767);
        }
        return ms.ToArray();
    }
}
