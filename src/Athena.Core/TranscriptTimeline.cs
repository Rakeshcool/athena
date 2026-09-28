// Timestamps for dictation takes — section 2 of feature_implement2.md.
//
// The Nemotron ASR server natively returns per-word timings when asked for
// response_format=verbose_json (verified live: { duration, language, text,
// words: [ { start, end, confidence, word } ] }). Athena's job is to move,
// persist and re-serialize that result — never to invent timing of its own.
// Everything here is a pure function so it tests like the other Core suites.
//
// All timestamps are SECONDS from the start of the source recording. Dual
// takes (mic + system loopback) share one timeline: each lane's words are
// shifted by that stream's capture-start offset relative to the take.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athena.Core;

/// <summary>One timed word from the ASR server's verbose_json decode.</summary>
public sealed record TimedWord(double Start, double End, string Word, double? Confidence = null);

/// <summary>A timed transcription: the model's plain text plus its word timeline.
/// Language is the server's detected-or-requested locale (e.g. "en-US") — the
/// verbose_json body echoes it, and it rides the stored JSON so exports can
/// reproduce the server's response shape.</summary>
public sealed record TimedTranscript(string Text, IReadOnlyList<TimedWord> Words,
    double? DurationSeconds = null, string? Language = null)
{
    public static readonly TimedTranscript Empty = new("", Array.Empty<TimedWord>());
    public bool HasWords => Words.Count > 0;
}

public static class TranscriptTimeline
{
    /// <summary>Gaps at least this long become paragraph breaks / new cues.</summary>
    public const double ParagraphGapSeconds = 1.5;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Parsing (server JSON → segments) ────────────────────────────────────

    /// <summary>Parse the ASR server's verbose_json body. Returns Empty for
    /// null/blank bodies so a malformed response degrades to the plain-text
    /// path instead of failing a take that would have succeeded untimed.</summary>
    public static TimedTranscript FromVerboseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return TimedTranscript.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            var duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetDouble() : (double?)null;

            var words = new List<TimedWord>();
            if (root.TryGetProperty("words", out var w) && w.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in w.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var start = item.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : -1;
                    var end = item.TryGetProperty("end", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : -1;
                    var word = item.TryGetProperty("word", out var wd) ? wd.GetString() ?? "" : "";
                    if (start < 0 || end < 0 || word.Length == 0) continue;
                    var conf = item.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                        ? c.GetDouble() : (double?)null;
                    words.Add(new TimedWord(start, end, word, conf));
                }
            }
            // The plain text is the model's own output (punctuation, casing,
            // stripped language tags); never rebuild it from the words.
            // Language: the server echoes the detected (or requested) locale —
            // capture it so exports reproduce the response shape.
            var language = root.TryGetProperty("language", out var l) ? l.GetString() : null;
            return new TimedTranscript(LanguageCatalog.StripLanguageTags(text).Trim(), words, duration,
                string.IsNullOrWhiteSpace(language) ? null : language);
        }
        catch (JsonException)
        {
            return TimedTranscript.Empty;
        }
    }

    // ── Timeline math ───────────────────────────────────────────────────────

    /// <summary>Shift a transcript's words so they sit on the take-wide
    /// timeline: a lane that began capturing `offsetSeconds` after the take
    /// started has its word times advanced by that amount.</summary>
    public static TimedTranscript Shift(TimedTranscript transcript, double offsetSeconds)
    {
        if (!transcript.HasWords || Math.Abs(offsetSeconds) < 0.0005) return transcript;
        return transcript with
        {
            Words = transcript.Words.Select(w => new TimedWord(
                Math.Max(0, w.Start + offsetSeconds),
                Math.Max(0, w.End + offsetSeconds),
                w.Word, w.Confidence)).ToList(),
        };
    }

    // ── Dual-take composition ───────────────────────────────────────────────

    /// <summary>Compose mic and system lanes chronologically: every word gets
    /// its lane label ("You:" / "Them:"), consecutive same-lane words join
    /// with spaces, lane switches and paragraph gaps become newlines. The
    /// result replaces MultiSourceComposer's fixed mic-first ordering — a
    /// meeting reads in the order it actually happened.</summary>
    public static string ComposeInterleaved(TimedTranscript? mic, TimedTranscript? system)
    {
        // Anything without usable words falls back to the classic composer:
        // mic first, blank line, system. The timed path is an upgrade, never
        // a regression — a verbose_json decode without words still composes.
        if (mic is not { HasWords: true } || system is not { HasWords: true })
            return MultiSourceComposer.Compose(mic?.Text, system?.Text);
        return ComposePairs(PairsOf(mic.Words, system.Words));
    }

    /// <summary>Merge two lanes' words onto one chronological timeline —
    /// stable on equal starts (the mic lane wins ties, keeping mic-then-system
    /// order for simultaneous words).</summary>
    public static IReadOnlyList<TimedWord> MergeWords(IReadOnlyList<TimedWord> mic, IReadOnlyList<TimedWord> system)
        => PairsOf(mic, system).Select(p => p.Word).ToList();

    /// <summary>Two-pointer merge: every word carries its lane explicitly —
    /// no value matching, no unstable sort.</summary>
    private static List<(TimedWord Word, bool IsMic)> PairsOf(
        IReadOnlyList<TimedWord> mic, IReadOnlyList<TimedWord> system)
    {
        var pairs = new List<(TimedWord, bool)>(mic.Count + system.Count);
        int i = 0, j = 0;
        while (i < mic.Count || j < system.Count)
        {
            if (j >= system.Count || (i < mic.Count && mic[i].Start <= system[j].Start))
                pairs.Add((mic[i++], true));
            else
                pairs.Add((system[j++], false));
        }
        return pairs;
    }

    private static string ComposePairs(IReadOnlyList<(TimedWord Word, bool IsMic)> pairs)
    {
        var sb = new System.Text.StringBuilder();
        for (var k = 0; k < pairs.Count; k++)
        {
            var (word, isMic) = pairs[k];
            if (k == 0)
            {
                sb.Append(isMic ? "You: " : "Them: ");
            }
            else
            {
                var (prev, prevIsMic) = pairs[k - 1];
                if (isMic != prevIsMic)
                    sb.Append('\n').Append(isMic ? "You: " : "Them: ");
                else if (word.Start - prev.End >= ParagraphGapSeconds)
                    sb.Append('\n'); // same speaker, a real pause: paragraph
                else
                    sb.Append(' ');
            }
            sb.Append(word.Word);
        }
        return sb.ToString();
    }

    /// <summary>Indices where a paragraph gap occurs: word i ends ≥ gapSeconds
    /// before word i+1 begins. Pause-aware cleanup uses these as hints.</summary>
    public static IReadOnlyList<int> SegmentBoundaries(IReadOnlyList<TimedWord> words, double gapSeconds = ParagraphGapSeconds)
    {
        var gaps = new List<int>();
        for (var i = 1; i < words.Count; i++)
        {
            if (words[i].Start - words[i - 1].End >= gapSeconds)
                gaps.Add(i);
        }
        return gaps;
    }

    // ── Persistence + export round-trips ────────────────────────────────────

    /// <summary>Serialize for the history DB's timeline column (JSON).</summary>
    public static string ToJson(TimedTranscript transcript) =>
        JsonSerializer.Serialize(transcript, JsonOpts);

    /// <summary>Deserialize a stored timeline. Returns Empty on garbage so a
    /// hand-edited DB never takes History down.</summary>
    public static TimedTranscript FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return TimedTranscript.Empty;
        try
        {
            var t = JsonSerializer.Deserialize<TimedTranscript>(json, JsonOpts);
            return t is null ? TimedTranscript.Empty : t;
        }
        catch (JsonException)
        {
            return TimedTranscript.Empty;
        }
    }

    // ── Server-faithful response formats (History export) ───────────────────
    // The ASR server's response-format dropdown offers plain JSON and JSON
    // with timestamps (verified live). History re-serializes the STORED
    // timeline into exactly those shapes — key names, nesting and order match
    // the server's own output, so an export is byte-shape-compatible with
    // what "Transcribe file → Download" would have produced.

    private sealed record TextOnlyResponse([property: JsonPropertyName("text")] string Text);

    private sealed record TimestampedResponse(
        [property: JsonPropertyName("duration")] double? Duration,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("task")] string Task,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("words")] IReadOnlyList<WordResponse> Words);

    private sealed record WordResponse(
        [property: JsonPropertyName("confidence")] double? Confidence,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("word")] string Word);

    /// <summary>The server's "JSON text" format: {"text": "…"}. Works on any
    /// row — it needs only the transcript, not word timings.</summary>
    public static string ToPlainTextJson(TimedTranscript transcript) =>
        JsonSerializer.Serialize(new TextOnlyResponse(transcript.Text));

    /// <summary>Serializer for the timestamped export: unlike storage, null
    /// fields are WRITTEN — the server's own response always carries every
    /// key (duration, language, task, confidence), so an export without a
    /// value keeps the same shape instead of dropping keys.</summary>
    private static readonly JsonSerializerOptions ExportOpts = new();

    /// <summary>The server's "JSON with timestamps" format: duration, language,
    /// task, text and the per-word words array (confidence/end/start/word).
    /// Requires a stored timeline; a wordless transcript yields empty words.</summary>
    public static string ToTimestampedJson(TimedTranscript transcript) =>
        JsonSerializer.Serialize(new TimestampedResponse(
            transcript.DurationSeconds,
            transcript.Language,
            "transcribe",
            transcript.Text,
            transcript.Words.Select(w => new WordResponse(w.Confidence, w.End, w.Start, w.Word)).ToList()),
            ExportOpts);

    /// <summary>Timestamp formatted for SRT (00:00:01,640).</summary>
    public static string FormatSrtTime(double seconds)
    {
        var s = Math.Max(0, seconds);
        var h = (int)(s / 3600);
        var m = (int)(s % 3600 / 60);
        var sec = (int)(s % 60);
        var ms = (int)Math.Round((s - Math.Floor(s)) * 1000);
        if (ms == 1000) { ms = 0; sec += 1; }
        return $"{h:00}:{m:00}:{sec:00},{ms:000}";
    }

    /// <summary>Timestamp for WebVTT (00:00:01.640 — dot separator).</summary>
    public static string FormatVttTime(double seconds) => FormatSrtTime(seconds).Replace(',', '.');

    /// <summary>Chunk a lane's words into subtitle cues of at most `maxWords`
    /// words, splitting early at paragraph gaps. Returns (start, end, text).</summary>
    public static IReadOnlyList<(double Start, double End, string Text)> Cues(
        IReadOnlyList<TimedWord> words, int maxWords = 12)
    {
        var cues = new List<(double, double, string)>();
        var i = 0;
        while (i < words.Count)
        {
            var j = i + 1;
            var taken = 1;
            while (j < words.Count && taken < maxWords &&
                   words[j].Start - words[j - 1].End < ParagraphGapSeconds)
            {
                j++;
                taken++;
            }
            cues.Add((words[i].Start, words[j - 1].End,
                string.Join(" ", words.Skip(i).Take(j - i).Select(w => w.Word))));
            i = j;
        }
        return cues;
    }

    /// <summary>SRT subtitles from a lane's words — the server's native
    /// subtitle shape, re-serialized from stored timings (no custom alignment
    /// anywhere; the ASR model did the timing).</summary>
    public static string ToSrt(IReadOnlyList<TimedWord> words)
    {
        var sb = new System.Text.StringBuilder();
        var n = 1;
        foreach (var (start, end, text) in Cues(words))
        {
            sb.AppendLine(n.ToString());
            sb.AppendLine($"{FormatSrtTime(start)} --> {FormatSrtTime(end)}");
            sb.AppendLine(text);
            sb.AppendLine();
            n++;
        }
        return sb.ToString();
    }

    /// <summary>WebVTT subtitles — same cues, VTT time syntax.</summary>
    public static string ToVtt(IReadOnlyList<TimedWord> words)
    {
        var sb = new System.Text.StringBuilder("WEBVTT");
        sb.AppendLine();
        sb.AppendLine();
        foreach (var (start, end, text) in Cues(words))
        {
            sb.AppendLine($"{FormatVttTime(start)} --> {FormatVttTime(end)}");
            sb.AppendLine(text);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
