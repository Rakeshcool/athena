// Live smoke test against the local Nemotron ASR (:8080) and llama.cpp LLM (:3000)
// servers discovered on this machine. Skips silently when a server is down so
// `dotnet test` stays green on machines without the stack; run explicitly with:
//   dotnet test --filter Category=Live

using System.Diagnostics;
using Athena.Core;
using Athena.Core.Clients;
using Xunit;
using Xunit.Abstractions;

namespace Athena.Core.Tests;

[Trait("Category", "Live")]
public class LiveServerSmokeTests
{
    private static bool AsrUp()
    {
        using var http = new HttpClient();
        return new LocalAsrClient(http, "http://127.0.0.1:8080").IsHealthyAsync(default).GetAwaiter().GetResult();
    }

    private static bool LlmUp()
    {
        using var http = new HttpClient();
        return new LocalLlmClient(http, "http://127.0.0.1:3000").IsHealthyAsync(default).GetAwaiter().GetResult();
    }

    /// <summary>Synthesizes speech with Windows SAPI so the ASR gets REAL spoken
    /// audio, not a sine wave — the closest thing to a dictation without a mic.</summary>
    private static string? SynthesizeSpeech(string text)
    {
        try
        {
            var wav = Path.Combine(Path.GetTempPath(), $"athena-tts-{Guid.NewGuid():N}.wav");
            var script = Path.Combine(Path.GetTempPath(), $"athena-tts-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(script, $"""
                Add-Type -AssemblyName System.Speech
                $s = New-Object System.Speech.Synthesis.SpeechSynthesizer
                $s.SetOutputToWaveFile('{wav.Replace("'", "''")}')
                $s.Speak('{text.Replace("'", "''")}')
                $s.Dispose()
                """);
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(30000);
            File.Delete(script);
            return File.Exists(wav) && new FileInfo(wav).Length > 1000 ? wav : null;
        }
        catch
        {
            return null;
        }
    }

    [SkippableFact]
    public async Task Asr_transcribes_real_speech()
    {
        Skip.IfNot(AsrUp(), "ASR server not running");
        var wav = SynthesizeSpeech("Hello, this is a test of the dictation system.");
        Assert.NotNull(wav);
        try
        {
            using var http = new HttpClient();
            var asr = new LocalAsrClient(http, "http://127.0.0.1:8080");
            var raw = await asr.TranscribeAsync(wav!, CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(raw), "ASR returned empty text for real speech");
            Assert.Contains("test", raw, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (wav is not null) File.Delete(wav);
        }
    }

    [SkippableFact]
    public async Task Llm_cleanup_collapses_self_correction()
    {
        Skip.IfNot(LlmUp(), "LLM server not running");
        using var http = new HttpClient();
        var llm = new LocalLlmClient(http, "http://127.0.0.1:3000");
        var pipeline = new FormattingPipeline(llm);
        var cleaned = await pipeline.ProcessAsync(
            "um lets meet at 1pm actually no make it 2pm",
            ct: CancellationToken.None);
        Assert.Contains("2", cleaned);
        Assert.DoesNotContain("1pm", cleaned);
        // Gate accepted the LLM output (not the raw fallback) — the fixture is
        // precisely PromptV1's live-probe example, so a faithful cleanup must pass.
        Assert.Equal("Let's meet at 2pm.", cleaned);
    }

    [SkippableFact]
    public async Task Llm_never_answers_a_question_shaped_dictation()
    {
        Skip.IfNot(LlmUp(), "LLM server not running");
        using var http = new HttpClient();
        var llm = new LocalLlmClient(http, "http://127.0.0.1:3000");
        var pipeline = new FormattingPipeline(llm);
        var raw = "what time is the standup tomorrow";
        var cleaned = await pipeline.ProcessAsync(raw, ct: CancellationToken.None);
        // Either the LLM cleaned it faithfully (question preserved) or the gate
        // fell back to raw — but an ANSWER must never survive.
        Assert.False(
            cleaned.StartsWith("The standup is", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("10:30"),
            $"LLM answered the dictation instead of cleaning it: {cleaned}");
        Assert.EndsWith("?", cleaned);
    }

    [SkippableFact]
    public async Task Full_pipeline_speech_to_clean_text()
    {
        Skip.IfNot(AsrUp(), "ASR server not running");
        Skip.IfNot(LlmUp(), "LLM server not running");
        var wav = SynthesizeSpeech("Let's meet at two p m actually no make it three p m on Thursday");
        Assert.NotNull(wav);
        try
        {
            using var http = new HttpClient();
            var asr = new LocalAsrClient(http, "http://127.0.0.1:8080");
            var llm = new LocalLlmClient(http, "http://127.0.0.1:3000");
            var pipeline = new FormattingPipeline(llm);

            var raw = await asr.TranscribeAsync(wav!, CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(raw));

            var cleaned = await pipeline.ProcessAsync(raw, ct: CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(cleaned));
            Assert.True(ValidationGate.Validate(raw, cleaned).Accepted || cleaned == raw,
                "pipeline output must be either gate-accepted or the raw fallback");
            // The self-correction ("actually no make it three") must have collapsed
            // to 3 — or the gate rejected and we kept raw (which still contains the
            // whole utterance, never a hallucinated answer).
            Assert.True(
                cleaned.Contains('3') || cleaned.Contains("three", StringComparison.OrdinalIgnoreCase),
                $"expected the corrected time in output, got: {cleaned}");
        }
        finally
        {
            if (wav is not null) File.Delete(wav);
        }
    }

    /// <summary>The verbose_json path against the REAL server: same text as
    /// the plain endpoint, plus per-word timings the exports are built from.
    /// This pins the live contract feature_implement2.md told us to verify
    /// before coding (response_format param, word shape, per-word timing).</summary>
    [SkippableFact]
    public async Task Asr_timed_decode_returns_words_and_identical_text()
    {
        Skip.IfNot(AsrUp(), "ASR server not running");
        var wav = SynthesizeSpeech("Athena stores the word timings for subtitle exports.");
        Assert.NotNull(wav);
        try
        {
            using var http = new HttpClient();
            var asr = new LocalAsrClient(http, "http://127.0.0.1:8080");
            var timed = await asr.TranscribeTimedAsync(wav!, CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(timed.Text));
            Assert.True(timed.HasWords, "verbose_json must carry a words array");
            Assert.True(timed.Words[0].End > timed.Words[0].Start, "word timings must be ordered start < end");
            Assert.True(timed.DurationSeconds is > 0.5, "duration should be reported");

            // The timeline is an ANNOTATION, never a different transcript.
            var plain = await asr.TranscribeAsync(wav!, CancellationToken.None);
            Assert.Equal(plain, timed.Text);

            // The server echoes the detected locale — exports reproduce it.
            Assert.False(string.IsNullOrWhiteSpace(timed.Language));
            Assert.StartsWith("en", timed.Language);

            // Round-trip through storage and export — the History path.
            var srt = TranscriptTimeline.ToSrt(timed.Words);
            Assert.Contains(" --> ", srt);
            Assert.Contains(timed.Words[0].Word, srt);

            // The two JSON exports re-serialize the stored shape verbatim.
            var timedJson = TranscriptTimeline.ToTimestampedJson(timed);
            Assert.Contains($"\"language\":\"{timed.Language}\"", timedJson);
            Assert.Contains("\"task\":\"transcribe\"", timedJson);
            Assert.Equal("{\"text\":\"" + plain + "\"}", TranscriptTimeline.ToPlainTextJson(timed));
        }
        finally
        {
            if (wav is not null) File.Delete(wav);
        }
    }
}
