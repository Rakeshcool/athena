// Unit tests for the timestamp module (feature_implement2.md section 2):
// verbose_json parsing, timeline shifting, interleaved composition, JSON
// round-trips and SRT/VTT serialization. Pure functions — no server needed.

using Athena.Core;
using Xunit;

namespace Athena.Core.Tests;

public class TranscriptTimelineTests
{
    private static TimedWord W(double s, double e, string w) => new(s, e, w);

    // ── FromVerboseJson ────────────────────────────────────────────────────

    [Fact]
    public void Parses_the_servers_verbose_json_shape()
    {
        // Captured live from the local Nemotron server (probe.wav, 2026-09-28).
        const string json = """
            {"duration":4.9686250686645508,"language":"en-US","task":"transcribe",
             "text":"Hello World, this is a timestamp probe.",
             "words":[
               {"confidence":1,"end":0.88,"start":0.64000000000000001,"word":"Hello"},
               {"confidence":1,"end":1.68,"start":0.95999999999999996,"word":"World,"}
             ]}
            """;
        var t = TranscriptTimeline.FromVerboseJson(json);
        Assert.Equal("Hello World, this is a timestamp probe.", t.Text);
        Assert.Equal(2, t.Words.Count);
        Assert.Equal(0.64, t.Words[0].Start, 3);
        Assert.Equal(0.88, t.Words[0].End, 3);
        Assert.Equal("Hello", t.Words[0].Word);
        Assert.Equal(1, t.Words[0].Confidence);
        Assert.Equal(4.9686, t.DurationSeconds!.Value, 3);
    }

    [Fact]
    public void Blank_or_garbage_body_parses_to_empty()
    {
        Assert.False(TranscriptTimeline.FromVerboseJson(null).HasWords);
        Assert.False(TranscriptTimeline.FromVerboseJson("").HasWords);
        Assert.False(TranscriptTimeline.FromVerboseJson("   ").HasWords);
        Assert.False(TranscriptTimeline.FromVerboseJson("{not json").HasWords);
        Assert.Equal("", TranscriptTimeline.FromVerboseJson("{}").Text);
    }

    [Fact]
    public void Strips_language_tags_and_skips_malformed_words()
    {
        const string json = """
            {"text":"<en-US> Hello there.",
             "words":[
               {"start":0.1,"end":0.2,"word":"Hello"},
               {"start":-1,"end":0.5,"word":"bad"},
               {"start":0.5,"end":0.9,"word":""},
               {"confidence":0.5,"start":0.3,"end":0.6,"word":"there."}
             ]}
            """;
        var t = TranscriptTimeline.FromVerboseJson(json);
        Assert.Equal("Hello there.", t.Text);
        Assert.Equal(2, t.Words.Count);
        Assert.Equal(0.5, t.Words[1].Confidence!.Value, 3);
    }

    [Fact]
    public void Captures_the_servers_detected_language()
    {
        // Shape captured live: the server echoes the detected (or requested)
        // locale even when the request sent none.
        const string json = """
            {"duration":23.066125869750977,"language":"vi-VN","task":"transcribe",
             "text":"Xin chào.","words":[{"confidence":1,"end":0.48,"start":0.4,"word":"Xin"}]}
            """;
        var t = TranscriptTimeline.FromVerboseJson(json);
        Assert.Equal("vi-VN", t.Language);
        Assert.Equal(23.066, t.DurationSeconds!.Value, 3);
        // A body without a language field still parses.
        Assert.Null(TranscriptTimeline.FromVerboseJson("{\"text\":\"hi\"}").Language);
    }

    // ── Shift ──────────────────────────────────────────────────────────────

    [Fact]
    public void Shift_moves_words_and_clamps_at_zero()
    {
        var t = new TimedTranscript("a b", new[] { W(1.0, 1.5, "a"), W(2.0, 2.5, "b") });
        var shifted = TranscriptTimeline.Shift(t, 3.0);
        Assert.Equal(4.0, shifted.Words[0].Start, 3);
        Assert.Equal(5.5, shifted.Words[1].End, 3);

        var clamped = TranscriptTimeline.Shift(t, -2.0); // would push 'a' below 0
        Assert.Equal(0.0, clamped.Words[0].Start, 3);
        Assert.Equal(0.0, clamped.Words[0].End, 3);
        Assert.Equal(0.0, clamped.Words[1].Start, 3);
    }

    [Fact]
    public void Shift_with_zero_offset_is_identity()
    {
        var t = new TimedTranscript("a", new[] { W(1.0, 1.5, "a") });
        Assert.Same(t, TranscriptTimeline.Shift(t, 0));
    }

    // ── MergeWords ─────────────────────────────────────────────────────────

    [Fact]
    public void Merge_interleaves_by_start_time()
    {
        var mic = new[] { W(0.0, 0.5, "you"), W(4.0, 4.5, "again") };
        var sys = new[] { W(1.0, 1.5, "them"), W(2.0, 2.5, "speaks") };
        var merged = TranscriptTimeline.MergeWords(mic, sys);
        Assert.Equal(new[] { "you", "them", "speaks", "again" }, merged.Select(w => w.Word));
    }

    [Fact]
    public void Merge_prefers_mic_lane_on_equal_starts()
    {
        var mic = new[] { W(1.0, 1.5, "mine") };
        var sys = new[] { W(1.0, 1.4, "theirs") };
        var merged = TranscriptTimeline.MergeWords(mic, sys);
        Assert.Equal("mine", merged[0].Word);
    }

    // ── ComposeInterleaved ─────────────────────────────────────────────────

    [Fact]
    public void Interleaved_compose_labels_and_orders_speakers()
    {
        var mic = new TimedTranscript("I think we should ship",
            new[] { W(0.0, 0.4, "I"), W(0.5, 0.9, "think"), W(1.0, 1.4, "we"), W(1.5, 2.2, "should"), W(2.3, 2.6, "ship.") });
        var sys = new TimedTranscript("Agreed, but not before lunch",
            new[] { W(3.0, 3.5, "Agreed,"), W(3.6, 3.8, "but"), W(3.9, 4.0, "not"), W(4.1, 4.6, "before"), W(4.7, 5.0, "lunch.") });
        var text = TranscriptTimeline.ComposeInterleaved(mic, sys);
        Assert.Equal("You: I think we should ship.\nThem: Agreed, but not before lunch.", text);
    }

    [Fact]
    public void Interleaved_compose_switches_lanes_mid_take()
    {
        var mic = new TimedTranscript("yes", new[] { W(0.0, 0.4, "Yes.") });
        var sys = new TimedTranscript("are you sure", new[] { W(0.2, 0.5, "Are"), W(0.6, 0.7, "you"), W(0.8, 1.0, "sure?") });
        var text = TranscriptTimeline.ComposeInterleaved(mic, sys);
        Assert.Equal("You: Yes.\nThem: Are you sure?", text);
    }

    [Fact]
    public void Big_gap_within_a_lane_becomes_a_paragraph()
    {
        // A pause INSIDE a lane matters when the take is interleaved: the gap
        // splits the speaker's own words into paragraphs (pause-aware output).
        var mic = new TimedTranscript("one two",
            new[] { W(0.0, 0.4, "One"), W(5.0, 5.4, "two.") });
        var sys = new TimedTranscript("okay", new[] { W(6.0, 6.3, "Okay.") });
        var text = TranscriptTimeline.ComposeInterleaved(mic, sys);
        Assert.Equal("You: One\ntwo.\nThem: Okay.", text);
    }

    [Fact]
    public void Untimed_or_empty_lanes_fall_back_to_classic_composer()
    {
        var timed = new TimedTranscript("hello", new[] { W(0.0, 0.5, "hello") });
        var untimed = new TimedTranscript("hello", Array.Empty<TimedWord>());
        // Neither lane has words → the fixed mic-first ordering survives.
        Assert.Equal("hello\n\nmeeting", TranscriptTimeline.ComposeInterleaved(untimed, new TimedTranscript("meeting", Array.Empty<TimedWord>())));
        // One lane timed, the other absent → plain single-side text, unchanged.
        Assert.Equal("hello", TranscriptTimeline.ComposeInterleaved(timed, null));
        Assert.Equal("hello", TranscriptTimeline.ComposeInterleaved(null, timed));
        Assert.Equal("", TranscriptTimeline.ComposeInterleaved(null, null));
    }

    // ── JSON round-trip ────────────────────────────────────────────────────

    [Fact]
    public void Timeline_json_round_trips()
    {
        var t = new TimedTranscript("Hello world.", new[] { W(0.64, 0.88, "Hello"), W(0.96, 1.68, "world.") }, 1.7, "en-US");
        var json = TranscriptTimeline.ToJson(t);
        var back = TranscriptTimeline.FromJson(json);
        Assert.Equal(t.Text, back.Text);
        Assert.Equal(2, back.Words.Count);
        Assert.Equal(t.Words[0], back.Words[0]);
        Assert.Equal(t.DurationSeconds, back.DurationSeconds);
        Assert.Equal(t.Language, back.Language);
    }

    // ── Server-faithful JSON exports ───────────────────────────────────────

    [Fact]
    public void Plain_json_export_matches_the_servers_json_text_format()
    {
        var t = new TimedTranscript("Hello world.", new[] { W(0.64, 0.88, "Hello") });
        Assert.Equal("""{"text":"Hello world."}""", TranscriptTimeline.ToPlainTextJson(t));
        // Works with no timeline at all — text is all it needs.
        Assert.Equal("""{"text":""}""", TranscriptTimeline.ToPlainTextJson(TimedTranscript.Empty));
    }

    [Fact]
    public void Timestamped_json_export_matches_the_server_response_shape()
    {
        var t = new TimedTranscript("Hello world.",
            new[] { W(0.64, 0.88, "Hello"), W(0.96, 1.68, "world.") }, 1.7, "en-US");
        var json = TranscriptTimeline.ToTimestampedJson(t);
        // Key names, order and nesting mirror the server's own body
        // (verified live: duration, language, task, text, words[] with
        // confidence/end/start/word).
        Assert.Equal("""
            {"duration":1.7,"language":"en-US","task":"transcribe","text":"Hello world.","words":[{"confidence":null,"end":0.88,"start":0.64,"word":"Hello"},{"confidence":null,"end":1.68,"start":0.96,"word":"world."}]}
            """.ReplaceLineEndings(""), json);
        // And it round-trips through the parser: export → parse → same data.
        var reparsed = TranscriptTimeline.FromVerboseJson(json);
        Assert.Equal(t.Text, reparsed.Text);
        Assert.Equal(t.Language, reparsed.Language);
        Assert.Equal(t.Words, reparsed.Words);
    }

    [Fact]
    public void Garbage_timeline_json_is_empty_not_a_crash()
    {
        Assert.False(TranscriptTimeline.FromJson(null).HasWords);
        Assert.False(TranscriptTimeline.FromJson("}{").HasWords);
    }

    // ── SRT / VTT ──────────────────────────────────────────────────────────

    [Fact]
    public void Srt_time_format_uses_comma()
    {
        Assert.Equal("00:00:00,640", TranscriptTimeline.FormatSrtTime(0.64));
        Assert.Equal("01:00:05,000", TranscriptTimeline.FormatSrtTime(3605));
        Assert.Equal("00:00:01,000", TranscriptTimeline.FormatSrtTime(0.9999));
        Assert.Equal("00:00:00,000", TranscriptTimeline.FormatSrtTime(-3));
    }

    [Fact]
    public void Vtt_time_format_uses_dot()
    {
        Assert.Equal("00:00:00.640", TranscriptTimeline.FormatVttTime(0.64));
        Assert.Equal("01:00:05.000", TranscriptTimeline.FormatVttTime(3605));
    }

    [Fact]
    public void Srt_output_groups_words_and_numbers_cues()
    {
        // 13 quick words → one cue of 12 (the cap), then a second cue.
        var words = Enumerable.Range(0, 13)
            .Select(k => W(k * 0.4, k * 0.4 + 0.3, $"w{k:00}"))
            .ToList();
        var srt = TranscriptTimeline.ToSrt(words).Replace("\r\n", "\n");
        var lines = srt.Split('\n');
        Assert.Equal("1", lines[0].Trim());
        Assert.Equal("00:00:00,000 --> 00:00:04,700", lines[1].Trim());
        Assert.Equal("w00 w01 w02 w03 w04 w05 w06 w07 w08 w09 w10 w11", lines[2].Trim());
        Assert.Contains("2\n00:00:04,800 --> 00:00:05,100\nw12", srt);
    }

    [Fact]
    public void Vtt_output_starts_with_webvtt_header()
    {
        var words = new[] { W(0.5, 0.9, "Hi.") };
        var vtt = TranscriptTimeline.ToVtt(words);
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("00:00:00.500 --> 00:00:00.900", vtt);
        Assert.Contains("Hi.", vtt);
    }

    [Fact]
    public void Cues_split_at_paragraph_gaps()
    {
        var words = new[] { W(0.0, 0.5, "a"), W(3.0, 3.5, "b"), W(3.6, 4.0, "c") };
        var cues = TranscriptTimeline.Cues(words, maxWords: 12);
        Assert.Equal(2, cues.Count);
        Assert.Equal("a", cues[0].Text);
        Assert.Equal("b c", cues[1].Text);
    }

    // ── SegmentBoundaries (pause-aware hints) ──────────────────────────────

    [Fact]
    public void Segment_boundaries_mark_gaps()
    {
        var words = new[] { W(0.0, 0.5, "a"), W(0.6, 1.0, "b"), W(3.0, 3.5, "c") };
        Assert.Equal(new[] { 2 }, TranscriptTimeline.SegmentBoundaries(words));
        Assert.Empty(TranscriptTimeline.SegmentBoundaries(new[] { W(0.0, 0.5, "a") }));
        Assert.Empty(TranscriptTimeline.SegmentBoundaries(Array.Empty<TimedWord>()));
    }
}
