// Ported from JotCore/Tests/JotCoreTests/TranscriptDiffTests.swift.
// The diff behind the HUD's "here is what got removed" reveal. It runs on every
// dictation and its output is shown to the user, so it has to be right about
// ordinary speech and, more importantly, has to fail quietly on speech it
// cannot align.

using Athena.Core;
using Xunit;

namespace Athena.Core.Tests;

public class TranscriptDiffTests
{
    private static string CutText(IReadOnlyList<TranscriptDiff.Segment> segments) =>
        string.Join("", segments.Where(s => s.IsCut).Select(s => s.Text)).Trim();

    private static string KeptText(IReadOnlyList<TranscriptDiff.Segment> segments) =>
        string.Join("", segments.Where(s => !s.IsCut).Select(s => s.Text));

    /// <summary>Joining every segment must reproduce the original exactly, or
    /// the HUD would render a sentence the user never said.</summary>
    private static void AssertLossless(IReadOnlyList<TranscriptDiff.Segment> segments, string original)
    {
        var joined = string.Join("", segments.Select(s => s.Text));
        Assert.True(joined == original,
            $"segments must reassemble the original verbatim text — expected {original}, got {joined}");
    }

    /// <summary>The landing page's own example.</summary>
    [Fact]
    public void Fillers_and_self_correction_are_cut()
    {
        var said = "umm, so let's meet at 1pm — actually, no, make it 2pm";
        var clean = "Let's meet at 2pm.";
        var segments = TranscriptDiff.Segments(said, clean);
        AssertLossless(segments, said);

        var cut = CutText(segments);
        Assert.Contains("umm", cut);
        Assert.Contains("actually", cut);
        Assert.Contains("1pm", cut);
        Assert.Contains("2pm", KeptText(segments));
    }

    /// <summary>The user's own sentence from dogfooding.</summary>
    [Fact]
    public void Real_dictation_keeps_the_substance()
    {
        var said = "hey um turn this call from 2pm to 3pm";
        var clean = "Hey, turn this call from 2pm to 3pm.";
        var segments = TranscriptDiff.Segments(said, clean);
        AssertLossless(segments, said);
        Assert.Contains("um", CutText(segments));
        var kept = KeptText(segments);
        foreach (var word in new[] { "turn", "call", "2pm", "3pm" })
            Assert.Contains(word, kept);
    }

    /// <summary>Punctuation and capitalisation are what cleanup ADDS. Comparing
    /// raw text would mark every word as removed and paint the sentence red.</summary>
    [Fact]
    public void Punctuation_and_casing_are_not_treated_as_edits()
    {
        var said = "lets meet at 2pm";
        var clean = "Let's meet at 2pm.";
        var segments = TranscriptDiff.Segments(said, clean);
        AssertLossless(segments, said);
        Assert.Equal("", CutText(segments));
    }

    [Fact]
    public void Nothing_removed_yields_one_kept_segment()
    {
        var said = "turn the call to 3pm";
        var segments = TranscriptDiff.Segments(said, "Turn the call to 3pm.");
        Assert.Equal(0, segments.Count(s => s.IsCut));
        AssertLossless(segments, said);
    }

    /// <summary>If the model rewrote wholesale rather than trimmed, there is no
    /// honest edit to show. Marking the entire sentence as deleted would be a
    /// bug rendered at full size, so it must fail to "no edit".</summary>
    [Fact]
    public void Unrelated_texts_show_no_edit()
    {
        var segments = TranscriptDiff.Segments("the quick brown fox", "completely different words here");
        var only = Assert.Single(segments);
        Assert.False(only.IsCut);
        AssertLossless(segments, "the quick brown fox");
    }

    /// <summary>Order matters: a repeated word must be matched in sequence, not
    /// by mere membership, or the first "1pm" would survive because a later one
    /// exists.</summary>
    [Fact]
    public void Repeated_words_match_in_order()
    {
        var said = "meet at 1pm no meet at 2pm";
        var segments = TranscriptDiff.Segments(said, "Meet at 2pm.");
        AssertLossless(segments, said);
        Assert.Contains("1pm", CutText(segments));
    }

    [Fact]
    public void Empty_inputs_are_safe()
    {
        Assert.Empty(TranscriptDiff.Segments("", "anything"));
        var segments = TranscriptDiff.Segments("some words", "");
        var only = Assert.Single(segments);
        Assert.False(only.IsCut);
    }

    /// <summary>Adjacent words of the same kind coalesce, so the UI animates a
    /// few runs rather than one span per word.</summary>
    [Fact]
    public void Adjacent_tokens_coalesce()
    {
        var segments = TranscriptDiff.Segments("umm uh so let's go", "Let's go.");
        AssertLossless(segments, "umm uh so let's go");
        Assert.True(segments.Count <= 2, $"three consecutive fillers should be one run, got {segments.Count}");
    }

    /// <summary>Whitespace must survive intact — the collapse animation removes
    /// the run including its trailing space, and a lost space would jam words
    /// together.</summary>
    [Fact]
    public void Whitespace_is_preserved()
    {
        var said = "hello   world  again";
        var segments = TranscriptDiff.Segments(said, "Hello world again.");
        AssertLossless(segments, said);
    }
}
