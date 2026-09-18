// Windows-port original: the streaming-vs-file first-word arbiter.
//
// The realtime stream decides each word the moment it is spoken — the FIRST
// word arrives with zero left context, so homophones (/raɪt/) are resolved by
// raw frequency and come out as "Right"/"Ryder" instead of "Write". The file
// endpoint sees the whole utterance at once and doesn't exhibit this. When
// enabled, both decodes run and the file reading wins ONLY where it matters:
// the utterance opening (first word plus the run that follows when the decodes
// disagree through the first sentence — the stream can cascade after a wrong
// start).

using System.Collections.Generic;
using System.Linq;

namespace Athena.Core;

public static class TranscriptArbiter
{
    private const int MaxGraftedWords = 12;

    /// <summary>Normalize for comparison: lowercase, strip punctuation and
    /// whitespace. "Write" vs "write." must compare equal.</summary>
    private static string Normalize(string word) =>
        new string(word.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static bool WordsMatch(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        return na.Length > 0 && na == nb;
    }

    /// <summary>Split on ANY whitespace (space, newline, tab) — realtime finals
    /// were historically newline-joined, and a token carrying an embedded '\n'
    /// would normalize to a different word than the file decode's clean token,
    /// faking a disagreement and corrupting the graft.</summary>
    private static List<string> Tokenize(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Pick between the streamed transcript and the file decode of the
    /// same recording. Returns the transcript to USE: the stream text with the
    /// file's opening grafted in when the FIRST word disagrees, otherwise the
    /// stream text unchanged. Never returns null/whitespace — on empty input the
    /// other transcript wins outright, so a failed file decode can never cost
    /// words.</summary>
    public static string Pick(string streamed, string fromFile)
    {
        if (string.IsNullOrWhiteSpace(fromFile)) return streamed;
        if (string.IsNullOrWhiteSpace(streamed)) return fromFile;

        var s = streamed.Trim();
        var f = fromFile.Trim();

        var sTokens = Tokenize(s);
        var fTokens = Tokenize(f);

        // Identical after normalization: keep the stream text verbatim (it has
        // the streaming capitalization/punctuation the cleanup stage expects).
        if (sTokens.Count == fTokens.Count && sTokens.Zip(fTokens, WordsMatch).All(m => m))
            return s;

        // The decodes differ somewhere. The fix targets the failure mode where
        // they differ from word 1; if the first words agree, the stream start is
        // already trustworthy (utterance-initial decoding didn't go wrong).
        if (sTokens.Count == 0 || fTokens.Count == 0) return s;
        if (WordsMatch(sTokens[0], fTokens[0])) return s;

        // First word differs — take the file's opening run as a block: everything
        // through the first sentence end (bounded). Grafting a single word risks a
        // chimera stitched from two incompatible readings of the same audio.
        var n = Math.Min(FirstSentenceLength(fTokens), fTokens.Count);
        var grafted = string.Join(" ", fTokens.Take(n));

        // Append the stream tail after those n words, if the stream had more to say.
        var remainder = sTokens.Count > n ? sTokens.Skip(n).ToList() : null;
        return remainder is null || remainder.Count == 0
            ? grafted
            : grafted + " " + string.Join(" ", remainder);
    }

    /// <summary>Words in the file decode's first sentence: everything through the
    /// first token carrying sentence-terminal punctuation (. ! ? …), bounded to
    /// MaxGraftedWords so an unpunctuated long dictation can't swallow everything.</summary>
    private static int FirstSentenceLength(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < Math.Min(tokens.Count, MaxGraftedWords); i++)
        {
            var last = tokens[i].Length > 0 ? tokens[i][^1] : '\0';
            if (last is '.' or '!' or '?' or '…')
                return i + 1;
        }
        return Math.Min(tokens.Count, MaxGraftedWords);
    }
}
