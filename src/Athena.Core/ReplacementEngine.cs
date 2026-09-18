// Ported from JotCore/Sources/FormattingPipeline/ReplacementEngine.swift.
// Deterministic post-model replacement layer: the dictionary's guarantee.
// The cleanup prompt *suggests* spellings to the model; this layer *enforces*
// the explicit wrong→right rules afterward. Longest-match-first, word-boundary,
// case-preserving (ALL-CAPS / Title / lower propagation).

using System.Text.RegularExpressions;

namespace Athena.Core;

public static class ReplacementEngine
{
    public readonly record struct Rule(string Wrong, string Right);

    public static string Apply(IReadOnlyList<Rule> rules, string text)
    {
        if (rules.Count == 0) return text;
        var result = text;
        // Longest wrong-form first so "gemini api" wins over "gemini".
        foreach (var rule in rules.OrderByDescending(r => r.Wrong.Length))
        {
            if (rule.Wrong.Length == 0) continue;
            // Lookarounds instead of \b: word boundaries silently never match when
            // the wrong form starts/ends with punctuation ("e.g.", "c++") (audit L21).
            var pattern = @"(?<![\w])" + Regex.Escape(rule.Wrong) + @"(?![\w])";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            // Replace via an evaluator over reversed-free single pass; matches are
            // non-overlapping by construction, so one pass is correct.
            result = regex.Replace(result, m => PropagateCase(m.Value, rule.Right, rule.Wrong));
        }
        return result;
    }

    /// <summary>"KUBERNETES"→"GRPC" stays caps; "Kubernetes"→"GRPC"… follows the rule's
    /// canonical casing unless the match was ALL-CAPS or the rule carries
    /// EXPLICIT casing. A rule is explicitly cased when its right side contains
    /// uppercase (gRPC, iPhone) — or when its WRONG side does ("NPM"→"npm" is a
    /// deliberate lowercase rule; ALL-CAPS propagation would silently undo it).</summary>
    public static string PropagateCase(string original, string replacement, string wrong = "")
    {
        var hasExplicitCasing =
            replacement.Skip(1).Any(char.IsUpper) ||
            (replacement.Length > 0 && char.IsUpper(replacement[0])) ||
            wrong.Any(char.IsUpper) ||
            (!string.IsNullOrEmpty(wrong) &&
             string.Equals(wrong, replacement, StringComparison.OrdinalIgnoreCase));
        if (hasExplicitCasing)
            return replacement; // dictionary term carries its own casing (gRPC, iPhone)
        if (original.Length > 1 && original == original.ToUpperInvariant())
            return replacement.ToUpperInvariant();
        if (original.Length > 0 && char.IsUpper(original[0]))
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        return replacement;
    }
}
