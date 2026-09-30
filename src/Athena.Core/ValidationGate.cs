// The "never insert garbage" gate (<1ms, runs between cleanup and insertion).
//
// Defends against the documented failure modes of prompted cleanup models:
// answering the dictation instead of cleaning it, paraphrase drift,
// hallucinated expansion, and content-dropping. On rejection the caller
// inserts the raw transcript (which already has punctuation — a high-quality
// fallback).

using System.Text.RegularExpressions;

namespace Athena.Core;

public static class ValidationGate
{
    public readonly record struct Verdict(bool Accepted, string? Reason)
    {
        public static Verdict Ok => new(true, null);
        public static Verdict Fail(string reason) => new(false, reason);
    }

    // Tuned against the live probe fixtures (see ValidationGateTests). Constants
    // deliberately generous: self-correction collapse legitimately halves a
    // transcript; answer-mode diverges in *content*, which containment catches.
    public const double MinLengthRatio = 0.20;
    public const double MaxLengthRatio = 1.60;
    public const double MinContainment = 0.50;
    public const double MinTrigramSimilarity = 0.55;

    private static readonly Regex AnswerPattern = new(
        @"^(sure|okay|certainly|of course|great question|here('s| is)|i can('|no)t|as an ai|i'm (sorry|an ai))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Strips model artifacts that are not failures: code fences,
    /// "CLEAN:"/"Transcript:" labels, wrapping quotes.</summary>
    public static string StripArtifacts(string text)
    {
        var s = text.Trim();
        if (s.StartsWith("```"))
        {
            s = Regex.Replace(s, @"^```[a-z]*\n?", "");
            s = s.Replace("```", "").Trim();
        }
        foreach (var label in new[] { "CLEAN:", "Clean:", "Transcript:", "TRANSCRIPT:" })
        {
            if (s.StartsWith(label))
                s = s[label.Length..].Trim();
        }
        if (s.Length > 1 && s.StartsWith('"') && s.EndsWith('"'))
            s = s[1..^1];
        return s;
    }

    public static Verdict Validate(string raw, string cleaned)
    {
        var rawWords = ContentWords(raw);
        var cleanWords = ContentWords(cleaned);

        if (cleanWords.Length == 0)
            return rawWords.Length == 0 ? Verdict.Ok : Verdict.Fail("empty_output");

        // Answer-preamble check: an answering model PREPENDS words that are not in
        // the dictation; a faithful cleanup preserves the speaker's opener. So the
        // pattern only fires when the cleaned text's first word differs from the
        // raw's first word — otherwise a dictation that starts with "Okay," would
        // be rejected for keeping its own opener.
        if (AnswerPattern.IsMatch(cleaned) &&
            !string.Equals(cleanWords[0], rawWords.FirstOrDefault(), StringComparison.OrdinalIgnoreCase))
        {
            return Verdict.Fail("answer_pattern");
        }

        var lower = cleaned.ToLowerInvariant();
        if (lower.Contains("as an ai") || lower.Contains("language model"))
            return Verdict.Fail("ai_selfreference");

        if (rawWords.Length == 0) return Verdict.Ok;

        var ratio = (double)cleanWords.Length / rawWords.Length;
        // Expansion is most dangerous on short raws (hallucinated content), so the
        // upper bound kicks in early; shrink is legitimate (filler/self-correction
        // collapse), so the lower bound only applies to longer raws.
        if (rawWords.Length >= 3 && ratio > MaxLengthRatio)
            return Verdict.Fail($"expansion_ratio_{ratio:0.00}");
        if (rawWords.Length >= 6 && ratio < MinLengthRatio)
            return Verdict.Fail($"shrink_ratio_{ratio:0.00}");

        var rawSet = rawWords.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contained = cleanWords.Count(w => rawSet.Contains(w));
        var containment = (double)contained / cleanWords.Length;
        var trigram = TrigramSimilarity(Normalize(raw), Normalize(cleaned));
        // Reject only when BOTH content signals diverge — cleanup legitimately
        // rewrites number words and punctuation words, hurting each individually.
        if (containment < MinContainment && trigram < MinTrigramSimilarity)
            return Verdict.Fail($"content_divergence_c{containment:0.00}_t{trigram:0.00}");

        return Verdict.Ok;
    }

    // Text plumbing

    /// <summary>Lowercased alphanumeric words with spoken numbers normalized to
    /// digits so "three" (raw) matches "3" (cleaned ITN output).</summary>
    public static string[] ContentWords(string text)
    {
        var norm = Normalize(text);
        return norm.Split(NonAlphanumeric, StringSplitOptions.RemoveEmptyEntries);
    }

    private static readonly char[] NonAlphanumeric =
        { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'', '(', ')',
            '[', ']', '{', '}', '-', '_', '/', '\\', '&', '#', '@', '*', '+', '=', '<', '>', '|', '~', '`', '^', '%', '$' };

    private static readonly (string Word, string Digit)[] NumberWords =
    {
        ("zero", "0"), ("one", "1"), ("two", "2"), ("three", "3"), ("four", "4"),
        ("five", "5"), ("six", "6"), ("seven", "7"), ("eight", "8"), ("nine", "9"),
        ("ten", "10"), ("eleven", "11"), ("twelve", "12"), ("twenty", "20"),
        ("thirty", "30"), ("forty", "40"), ("fifty", "50"), ("hundred", "100"),
    };

    public static string Normalize(string text)
    {
        var s = text.ToLowerInvariant();
        foreach (var (word, digit) in NumberWords)
            s = Regex.Replace(s, $@"\b{word}\b", digit);
        return s;
    }

    public static double TrigramSimilarity(string a, string b)
    {
        var ta = Trigrams(a);
        var tb = Trigrams(b);
        if (ta.Count == 0 || tb.Count == 0) return a == b ? 1 : 0;
        var intersection = ta.Intersect(tb).Count();
        return (double)intersection / Math.Min(ta.Count, tb.Count);
    }

    private static System.Collections.Generic.IReadOnlySet<string> Trigrams(string s)
    {
        var chars = s.Where(c => !char.IsWhiteSpace(c)).ToArray();
        if (chars.Length < 3)
            return chars.Length == 0
                ? new HashSet<string>()
                : new HashSet<string> { new string(chars) };
        var set = new HashSet<string>();
        for (var i = 0; i <= chars.Length - 3; i++)
            set.Add(new string(chars, i, 3));
        return set;
    }
}
