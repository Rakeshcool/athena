// Windows-port original: the pure combiner for multi-source dictation. A
// session may capture the microphone AND the system output device (WASAPI
// loopback) at once — each decodes to its own transcript — and the user gets
// ONE insertion: mic text first, a blank line, then the system-audio text.
//
// The separator is composed HERE, not by the LLM: cleanup runs per source
// (two parallel calls) and the parts are joined deterministically afterwards.
// Letting the cleanup model handle the join risks it merging the paragraphs
// — the blank line is the only thing that tells the user which words were
// theirs and which came from the machine.

namespace Athena.Core;

public static class MultiSourceComposer
{
    /// <summary>A blank line — visually a paragraph break in every editor.</summary>
    public const string Separator = "\n\n";

    /// <summary>Mic transcript first, system-audio second, joined by a blank
    /// line. Whitespace-only parts are treated as absent; nulls are absent.
    /// One empty side yields the other side verbatim (trimmed) — a mic-only
    /// session composes to exactly what a single-source session produced
    /// before this feature existed.</summary>
    public static string Compose(string? mic, string? system)
    {
        var m = (mic ?? "").Trim();
        var s = (system ?? "").Trim();
        if (m.Length == 0 && s.Length == 0) return "";
        if (s.Length == 0) return m;
        if (m.Length == 0) return s;
        return m + Separator + s;
    }
}
