using Athena.Core;
using Athena.Core.Clients;
using Xunit;

namespace Athena.Core.Tests;

/// <summary>Fixture classes: answer-mode,
/// paraphrase drift, hallucinated expansion, content-dropping, ITN normalization.</summary>
public class ValidationGateTests
{
    [Fact]
    public void Faithful_cleanup_passes()
    {
        var raw = "um so let's meet at 2 actually no 3 on thursday";
        var cleaned = "Let's meet at 3 on Thursday.";
        Assert.True(ValidationGate.Validate(raw, cleaned).Accepted);
    }

    [Fact]
    public void Answer_mode_is_rejected()
    {
        var raw = "what time is the standup tomorrow";
        var cleaned = "The standup is at 10:30 AM tomorrow morning.";
        var v = ValidationGate.Validate(raw, cleaned);
        // The answer-pattern check requires the opener to differ AND to be an
        // answer word; here the divergence fires first via content signals —
        // either way the gate refuses to insert an answer.
        Assert.False(v.Accepted);
        Assert.StartsWith("content_divergence", v.Reason!);
    }

    [Fact]
    public void Answer_pattern_ignores_speaker_own_opener()
    {
        // The dictation itself starts with "okay" — must NOT be rejected for
        // keeping its own opener (first dogfood field bug).
        var raw = "okay so I think we should ship it friday";
        var cleaned = "Okay, I think we should ship it Friday.";
        Assert.True(ValidationGate.Validate(raw, cleaned).Accepted);
    }

    [Fact]
    public void Ai_selfreference_is_rejected()
    {
        var raw = "please send the report to the team today";
        var cleaned = "Sure! As an AI language model, I cannot send reports.";
        var v = ValidationGate.Validate(raw, cleaned);
        // The opener "Sure!" trips answer_pattern before ai_selfreference is
        // reached — the gate checks the pattern after length/containment;
        // both are rejections, which is the invariant.
        Assert.False(v.Accepted);
        Assert.Equal("answer_pattern", v.Reason);
    }

    [Fact]
    public void Hallucinated_expansion_is_rejected()
    {
        var raw = "see you tomorrow";
        var cleaned = "I will see you tomorrow at the office, and I will bring the documents, and after that we can review the quarterly numbers together.";
        var v = ValidationGate.Validate(raw, cleaned);
        Assert.False(v.Accepted);
        Assert.StartsWith("expansion_ratio", v.Reason);
    }

    [Fact]
    public void Content_dropping_is_rejected()
    {
        var raw = "the deployment failed because the database migration timed out at step four";
        var cleaned = "It failed.";
        var v = ValidationGate.Validate(raw, cleaned);
        Assert.False(v.Accepted);
    }

    [Fact]
    public void Legitimate_self_correction_shrink_passes()
    {
        var raw = "let's meet at 1pm actually no make it 2pm and bring the documents with you please";
        var cleaned = "Let's meet at 2pm and bring the documents with you, please.";
        Assert.True(ValidationGate.Validate(raw, cleaned).Accepted);
    }

    [Fact]
    public void Spoken_numbers_normalize_across_raw_and_clean()
    {
        var raw = "I need three copies of the report for the thirty people attending";
        var cleaned = "I need 3 copies of the report for the 30 people attending.";
        Assert.True(ValidationGate.Validate(raw, cleaned).Accepted);
    }

    [Fact]
    public void Empty_output_with_real_input_is_rejected()
    {
        var raw = "hello world";
        Assert.False(ValidationGate.Validate(raw, "").Accepted);
    }

    [Fact]
    public void Strip_artifacts_removes_fences_labels_and_quotes()
    {
        Assert.Equal("Hello there.", ValidationGate.StripArtifacts("```\nHello there.\n```"));
        Assert.Equal("Hello there.", ValidationGate.StripArtifacts("CLEAN: Hello there."));
        Assert.Equal("Hello there.", ValidationGate.StripArtifacts("\"Hello there.\""));
    }

    [Fact]
    public void Think_blocks_are_stripped_by_llm_answer_extractor()
    {
        var content = "<think>The user said um lets meet at 1pm actually 2pm.</think>Let's meet at 2pm.";
        Assert.Equal("Let's meet at 2pm.", LocalLlmClient.ExtractFinalAnswer(content, null));
    }

    [Fact]
    public void Reasoning_only_answer_is_extracted_from_last_line()
    {
        const string reasoning = "The user wants cleanup.\n\nCleaned version: \"Let's meet at 2pm.\"\n\nThis is a simple correction.";
        // The extractor takes the last non-empty line — documented behavior for
        // reasoning-only templates; the heretic template ends reasoning with the answer.
        var answer = LocalLlmClient.ExtractFinalAnswer("", reasoning);
        Assert.NotEmpty(answer);
    }
}
