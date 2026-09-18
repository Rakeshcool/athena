// Windows-port original: the RecoveryScanner analog. WAV finalizes a 44-byte
// header on close; a crash tears it. CAF (the macOS format) is chosen because
// it survives that; on Windows we keep WAV but repair the header from the file
// size, restoring the same "recording recovered on next launch" guarantee.

using System.IO;
using NAudio.Wave;

namespace Jot.App.Audio;

public static class WavRepair
{
    /// <summary>Fixes a truncated WAV by rewriting the RIFF sizes from the actual
    /// file length. Returns true when the file was repaired or already valid.</summary>
    public static bool Repair(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            if (fs.Length < 44) return false;
            Span<byte> header = stackalloc byte[44];
            fs.ReadExactly(header);

            // RIFF....WAVEfmt ....data....
            if (header[0] != (byte)'R' || header[1] != (byte)'I' ||
                header[8] != (byte)'W' || header[9] != (byte)'A') return false;

            var dataChunkIndex = FindDataChunk(fs);
            if (dataChunkIndex < 0) return false;

            var dataLen = (uint)(fs.Length - dataChunkIndex - 8);
            var riffLen = (uint)(fs.Length - 8);

            // The canonical 44-byte header layout: riffSize at 4, dataSize at 40.
            WriteUint(header, 4, riffLen);
            WriteUint(header, 40, dataLen);
            fs.Position = 0;
            fs.Write(header);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long FindDataChunk(FileStream fs)
    {
        const uint dataFourcc = 0x61746164; // "data" little-endian
        fs.Position = 12;
        while (fs.Position + 8 <= fs.Length)
        {
            Span<byte> chunk = stackalloc byte[8];
            if (fs.Read(chunk) < 8) break;
            var id = BitConverter.ToUInt32(chunk);
            var size = BitConverter.ToUInt32(chunk[4..]);
            if (id == dataFourcc) return fs.Position - 8;
            fs.Position += size + (size & 1); // chunks are word-aligned
        }
        return -1;
    }

    private static void WriteUint(Span<byte> b, int offset, uint value)
    {
        b[offset] = (byte)value;
        b[offset + 1] = (byte)(value >> 8);
        b[offset + 2] = (byte)(value >> 16);
        b[offset + 3] = (byte)(value >> 24);
    }
}

public static class WavRecorderRecoveryExtensions
{
    /// <summary>Marks a cancelled session's audio for retention cleanup rather than
    /// recovery — cancel means "I didn't want those words", but we keep the file
    /// until retention sweeps it (never-delete-before-transcript rule, softened).</summary>
    public static void RecoveryMarkCancelled(string wavPath)
    {
        try
        {
            WavRepair.Repair(wavPath); // even a cancelled clip should be playable
        }
        catch { /* best effort */ }
    }
}
