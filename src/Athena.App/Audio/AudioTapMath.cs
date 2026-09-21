// Windows-port original: the buffer math both capture paths share. The
// microphone recorder (WavRecorder) and the system-audio recorder
// (SystemAudioRecorder) receive WASAPI shared-mode buffers in the device's
// native mix format and need the same two things per buffer: an RMS level for
// the HUD/trailing-speech logic, and a PCM16 MONO conversion at the native
// rate for the realtime tap (the web client's contract — native-rate mono
// PCM16, declared rate matches, the server resamples internally).
//
// HOT PATH (~every 10ms per source): zero LINQ, allocations only for the
// emitted mono chunk and the torn-frame carry.

using NAudio.Wave;

namespace Athena.App.Audio;

public static class AudioTapMath
{
    /// <summary>RMS of the buffer as a 0…1-ish magnitude, format-aware
    /// (IEEE float32 vs PCM16). Callers normalize through AudioLevelCurve.</summary>
    public static float Rms(byte[] buffer, int bytes, WaveFormat format)
    {
        if (bytes <= 0 || format.Channels <= 0) return 0;
        double sum = 0;
        var count = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var sampleCount = bytes / 4;
            for (var i = 0; i < sampleCount; i++)
            {
                var sample = BitConverter.ToSingle(buffer, i * 4);
                sum += sample * sample;
            }
            count = sampleCount;
        }
        else
        {
            var sampleCount = bytes / 2;
            for (var i = 0; i < sampleCount; i++)
            {
                var sample = BitConverter.ToInt16(buffer, i * 2) / 32768f;
                sum += sample * sample;
            }
            count = sampleCount;
        }
        return count == 0 ? 0 : MathF.Sqrt((float)(sum / count));
    }

    /// <summary>Convert one capture buffer to PCM16 MONO at the NATIVE rate and
    /// emit it. Downmix (L+R)/2; a torn sample frame at the buffer boundary is
    /// carried into the next call via <paramref name="carry"/>. IEEE float32
    /// sources convert directly; int16 sources are normalized to −1…1 first —
    /// both then round(x * 32767) clamped, exactly the server web client's
    /// conversion. Any failure drops ONLY this buffer (the durable file is
    /// untouched), so the caller may call this inside its best-effort catch.</summary>
    public static void DownmixToMonoPcm16(
        byte[] buffer, int bytes, WaveFormat source, ref byte[] carry, Action<byte[]> emit)
    {
        if (bytes <= 0) return;

        var frameSize = source.Channels * (source.BitsPerSample / 8);
        if (frameSize <= 0) return;

        var total = carry.Length + bytes;
        var whole = total - (total % frameSize);
        var carryLen = total - whole;

        byte[] input;
        if (carry.Length == 0)
        {
            input = buffer; // aligned: no copy at all
        }
        else
        {
            input = new byte[total];
            Buffer.BlockCopy(carry, 0, input, 0, carry.Length);
            Buffer.BlockCopy(buffer, 0, input, carry.Length, bytes);
        }

        // Stash the tail (whole..total) as the next carry.
        if (carryLen > 0)
        {
            if (carry.Length != carryLen) carry = new byte[carryLen];
            Buffer.BlockCopy(input, whole, carry, 0, carryLen);
        }
        else carry = Array.Empty<byte>();

        var frames = whole / frameSize;
        if (frames <= 0) return;
        var mono = new byte[frames * 2];
        var channels = source.Channels;
        var isFloat = source.BitsPerSample == 32
            && source.Encoding == WaveFormatEncoding.IeeeFloat;
        var sampleBytes = source.BitsPerSample / 8;
        for (var f = 0; f < frames; f++)
        {
            double acc = 0;
            for (var c = 0; c < channels; c++)
            {
                var idx = f * frameSize + c * sampleBytes;
                acc += isFloat
                    ? BitConverter.ToSingle(input, idx)
                    : BitConverter.ToInt16(input, idx) / 32768.0;
            }
            var v = (int)Math.Clamp(
                (int)Math.Round(acc / channels * 32767.0),
                short.MinValue, short.MaxValue);
            mono[f * 2] = (byte)(v & 0xFF);
            mono[f * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        emit(mono);
    }
}
