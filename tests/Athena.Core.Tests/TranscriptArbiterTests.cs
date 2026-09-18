// Tests for the streaming-vs-file first-word arbiter. The failure it targets is
// real user data: "Write a program" heard as "Right/Ryder program" by the zero-
// left-context stream, while the file decode of the same audio is correct.

using Athena.Core;
using Xunit;

namespace Athena.Core.Tests;

public class TranscriptArbiterTests
{
    [Fact]
    public void First_word_homophone_is_corrected_by_the_file_decode()
    {
        // Real user data: the stream heard "Right", the file heard "Write".
        var picked = TranscriptArbiter.Pick(
            streamed: "Right program to find magic number",
            fromFile: "Write a program to find magic number");

        Assert.StartsWith("Write a program", picked);
        // The stream tail beyond the file's first sentence is preserved.
        Assert.EndsWith("find magic number", picked);
        Assert.DoesNotContain("Right", picked);
    }

    [Fact]
    public void Cascade_after_a_wrong_start_grafts_the_file_opening()
    {
        // The stream cascaded: two wrong words, then recovered.
        var picked = TranscriptArbiter.Pick(
            streamed: "Ryder program to find the magic number",
            fromFile: "Write a program to find the magic number.");

        Assert.StartsWith("Write a program", picked);
        Assert.DoesNotContain("Ryder", picked);
    }

    [Fact]
    public void First_word_agreement_leaves_the_stream_text_alone()
    {
        const string streamed = "I have a meeting at five o'clock no actually make it six.";
        const string fromFile = "I have a meeting at five o'clock no actually make it six o'clock";

        var picked = TranscriptArbiter.Pick(streamed, fromFile);

        Assert.Equal(streamed, picked);
    }

    [Fact]
    public void Normalization_ignores_punctuation_and_case()
    {
        // Same words, different punctuation: identical transcript, no graft.
        var picked = TranscriptArbiter.Pick(
            streamed: "Hello, where are you?",
            fromFile: "hello where are you");

        Assert.Equal("Hello, where are you?", picked);
    }

    [Fact]
    public void Failed_file_decode_never_costs_words()
    {
        Assert.Equal("Right program", TranscriptArbiter.Pick("Right program", ""));
        Assert.Equal("Right program", TranscriptArbiter.Pick("Right program", "   "));
        Assert.Equal("file text", TranscriptArbiter.Pick("", "file text"));
    }

    [Fact]
    public void Sentence_bounded_graft_does_not_swallow_a_long_dictation()
    {
        // No sentence-terminal punctuation in the file decode for many words:
        // the graft is bounded at 12 words, and the stream tail follows.
        var fromFile = string.Join(" ", Enumerable.Range(1, 30).Select(i => $"word{i}"));
        var streamed = string.Join(" ", Enumerable.Range(1, 30).Select(i => $"word{i}"));

        var picked = TranscriptArbiter.Pick(streamed, fromFile);
        // First words differ? No — they're identical, so it's the passthrough case.
        Assert.Equal(streamed, picked);
    }

    [Fact]
    public void Bounded_graft_keeps_tail_when_first_word_differs_without_punctuation()
    {
        var fromFile = string.Join(" ", Enumerable.Range(1, 30).Select(i => $"f{i}"));
        var streamed = string.Join(" ", Enumerable.Range(1, 30).Select(i => $"s{i}"));

        var picked = TranscriptArbiter.Pick(streamed, fromFile);

        Assert.StartsWith("f1 f2 f3 f4 f5 f6 f7 f8 f9 f10 f11 f12", picked);
        Assert.Contains("s13", picked);
        Assert.EndsWith("s30", picked);
    }

    [Fact]
    public void File_opening_shorter_than_the_stream_takes_the_file_opening_whole()
    {
        var picked = TranscriptArbiter.Pick(
            streamed: "Right two nine magic number and then some more text here",
            fromFile: "Write a program");

        Assert.StartsWith("Write a program", picked);
        Assert.EndsWith("some more text here", picked);
    }
}
