// Ported from JotCore/Sources/FormattingPipeline/TranscriptDiff.swift.
// Works out which words cleanup removed, so the HUD can show the edit rather
// than just the result: "umm, so let's meet at 1pm — actually, no, make it 2pm"
// → "Let's meet at 2pm." Showing the edit is far more convincing than showing
// only the tidy answer, because the tidy answer alone looks like the user
// simply spoke well.

using System.Text.RegularExpressions;

namespace Athena.Core;

public static class TranscriptDiff
{
    public sealed record Segment(string Text, bool IsCut);

    /// <summary>Splits <paramref name="verbatim"/> into kept and cut runs,
    /// aligned against <paramref name="cleaned"/>.</summary>
    /// <remarks>Returns a single kept segment when there is nothing useful to
    /// show — no overlap at all, or an empty side. A diff that marks the entire
    /// sentence as removed is not an insight, it is a bug rendered at full size,
    /// so it fails to "no edit" rather than to something dramatic.</remarks>
    public static IReadOnlyList<Segment> Segments(string verbatim, string cleaned)
    {
        var saidTokens = Tokenize(verbatim);
        var keptTokens = Tokenize(cleaned);
        if (saidTokens.Count == 0 || keptTokens.Count == 0)
            return verbatim.Length == 0
                ? Array.Empty<Segment>()
                : new[] { new Segment(verbatim, IsCut: false) };

        var keptFlags = MatchFlags(saidTokens, keptTokens);

        // If nothing survived, the two texts are unrelated — most likely the
        // model rewrote wholesale rather than trimmed. Show it as unedited.
        if (!keptFlags.Contains(true))
            return new[] { new Segment(verbatim, IsCut: false) };

        // Coalesce adjacent tokens of the same kind so the UI animates a handful
        // of runs rather than one span per word.
        var segments = new List<Segment>();
        var current = "";
        var currentIsCut = !keptFlags[0];
        for (var index = 0; index < saidTokens.Count; index++)
        {
            var token = saidTokens[index];
            var isCut = !keptFlags[index];
            if (isCut == currentIsCut)
            {
                current += token.Raw;
            }
            else
            {
                if (current.Length > 0)
                    segments.Add(new Segment(current, currentIsCut));
                current = token.Raw;
                currentIsCut = isCut;
            }
        }
        if (current.Length > 0)
            segments.Add(new Segment(current, currentIsCut));
        return segments;
    }

    internal sealed record Token(string Raw, string Key);

    /// <summary>The word plus whatever whitespace followed it, so joining the
    /// raws reproduces the original string exactly. <see cref="Key"/> is
    /// lowercased and stripped of punctuation, for comparison only — cleanup
    /// adds punctuation and capitalisation, so comparing raw text would mark
    /// every word as cut.</summary>
    internal static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var word = "";
        var trailing = "";
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                trailing += character;
            }
            else
            {
                if (trailing.Length > 0)
                {
                    if (word.Length > 0)
                    {
                        tokens.Add(new Token(word + trailing, Normalize(word)));
                        word = "";
                    }
                    else if (tokens.Count > 0)
                    {
                        var last = tokens[^1];
                        tokens[^1] = new Token(last.Raw + trailing, last.Key);
                    }
                    trailing = "";
                }
                word += character;
            }
        }
        if (word.Length > 0 || trailing.Length > 0)
            tokens.Add(new Token(word + trailing, Normalize(word)));
        return tokens.Where(t => t.Raw.Length > 0).ToList();
    }

    internal static string Normalize(string word) =>
        new(word.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Longest common subsequence over the normalized keys. Every said
    /// token on the LCS is kept; everything else was removed.</summary>
    /// <remarks>LCS rather than a set membership test because order carries
    /// meaning here: in "meet at 1pm, actually no, make it 2pm" the word "meet"
    /// appears once and must stay put, and a naive contains-check would also
    /// wrongly keep the first "1pm" if the final happened to mention 1pm
    /// elsewhere.</remarks>
    internal static bool[] MatchFlags(List<Token> said, List<Token> kept)
    {
        int n = said.Count, m = kept.Count;
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                table[i, j] = said[i].Key == kept[j].Key && said[i].Key.Length > 0
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var flags = new bool[n];
        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (said[a].Key == kept[b].Key && said[a].Key.Length > 0)
            {
                flags[a] = true;
                a++; b++;
            }
            else if (table[a + 1, b] >= table[a, b + 1])
            {
                a++;
            }
            else
            {
                b++;
            }
        }
        return flags;
    }
}
