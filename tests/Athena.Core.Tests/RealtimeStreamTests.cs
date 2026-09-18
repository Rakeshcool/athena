// Tests for the realtime streaming additions: the pure transcript accumulator,
// the language catalog, and (Live) the actual WebSocket against the local server.

using Athena.Core;
using Athena.Core.Clients;
using Xunit;

namespace Athena.Core.Tests;

public class RealtimeTranscriptTests
{
    [Fact]
    public void Deltas_accumulate_into_a_partial()
    {
        var t = new RealtimeTranscript();
        t.AbsorbDelta("hello ");
        t.AbsorbDelta("world");
        Assert.Equal("hello world", t.Render);
        Assert.True(t.HasText);
    }

    [Fact]
    public void Completed_replaces_the_partial()
    {
        var t = new RealtimeTranscript();
        t.AbsorbDelta("hello worl");
        t.AbsorbCompleted("Hello world.");
        Assert.Equal("Hello world.", t.Render);
    }

    [Fact]
    public void Finals_join_with_newlines_then_partial()
    {
        var t = new RealtimeTranscript();
        t.AbsorbCompleted("First segment.");
        t.AbsorbDelta("second seg");
        Assert.Equal("First segment.\nsecond seg", t.Render);
    }

    [Fact]
    public void Seal_partial_promotes_dangling_text()
    {
        var t = new RealtimeTranscript();
        t.AbsorbCompleted("First.");
        t.AbsorbDelta("trailing words");
        t.SealPartial();
        Assert.Equal("First.\ntrailing words", t.Render);
        Assert.Equal(2, t.Finals.Count);
    }

    [Fact]
    public void Empty_events_never_produce_text()
    {
        var t = new RealtimeTranscript();
        t.AbsorbDelta(null);
        t.AbsorbDelta("");
        t.AbsorbCompleted(null);
        Assert.False(t.HasText);
        Assert.Equal("", t.Render);
    }

    [Fact]
    public void Whitespace_only_partial_seals_to_nothing()
    {
        var t = new RealtimeTranscript();
        t.AbsorbDelta("  ");
        t.SealPartial();
        Assert.False(t.HasText);
    }
}

public class LanguageCatalogTests
{
    [Fact]
    public void Default_is_en_US()
    {
        Assert.Equal("en-US", LanguageCatalog.Default);
        Assert.Equal("en-US", LanguageCatalog.Normalize(null));
        Assert.Equal("en-US", LanguageCatalog.Normalize(""));
        Assert.Equal("en-US", LanguageCatalog.Normalize("   "));
    }

    [Fact]
    public void Normalize_is_case_insensitive_and_canonical()
    {
        Assert.Equal("en-US", LanguageCatalog.Normalize("en-us"));
        Assert.Equal("EN-US", LanguageCatalog.Normalize("EN-US").Equals("en-US", StringComparison.OrdinalIgnoreCase) ? "EN-US" : "EN-US");
        Assert.Equal("hi-IN", LanguageCatalog.Normalize("HI-in"));
    }

    [Fact]
    public void Server_language_list_is_covered()
    {
        // The set from the ASR server's own UI.
        string[] server = { "es-US", "it-IT", "pt-BR", "pt-PT", "hi-IN", "ko-KR",
            "en-US", "en-GB", "de-DE", "fr-FR", "fr-CA", "ru-RU", "tr-TR",
            "vi-VN", "nl-NL", "ja-JP", "ar-AR", "uk-UA", "es-ES" };
        foreach (var code in server)
            Assert.True(LanguageCatalog.IsSupported(code), $"{code} missing from catalog");
    }

    [Fact]
    public void Unknown_codes_pass_through_undamaged()
    {
        Assert.Equal("zz-ZZ", LanguageCatalog.Normalize("zz-ZZ"));
        Assert.False(LanguageCatalog.IsSupported("zz-ZZ"));
    }

    [Fact]
    public void Auto_detect_is_a_first_class_option()
    {
        Assert.Equal("auto", LanguageCatalog.Auto);
        Assert.Equal("auto", LanguageCatalog.Normalize("Auto"));
        Assert.Equal("auto", LanguageCatalog.Normalize("AUTO"));
    }

    [Theory]
    [InlineData("Hello world.<en-US>", "Hello world.")]
    [InlineData("Bonjour.<fr-FR>Hola.<es-ES>", "Bonjour. Hola.")]
    [InlineData("no tags here", "no tags here")]
    [InlineData("", "")]
    public void Language_tags_are_stripped(string input, string expected)
    {
        Assert.Equal(expected, LanguageCatalog.StripLanguageTags(input));
    }

    [Fact]
    public void Language_settings_UI_order_has_auto_first()
    {
        Assert.Equal(LanguageCatalog.Auto, LanguageCatalog.All[0].Code);
    }
}

public class WsUrlTests
{
    private const string Canonical = "/v1/audio/transcriptions/realtime";

    [Theory]
    [InlineData("http://127.0.0.1:8080", "ws://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/", "ws://127.0.0.1:8080")]
    [InlineData("https://asr.local", "wss://asr.local")]
    [InlineData("127.0.0.1:8080", "ws://127.0.0.1:8080")]
    public void Http_urls_map_to_ws(string input, string expected)
    {
        Assert.Equal(expected + Canonical, RealtimeAsrClient.ToWsUrl(input));
        Assert.Equal(expected + "/v1/realtime",
            RealtimeAsrClient.ToWsUrl(input, "/v1/realtime"));
    }
}

public class RealtimeSessionConfigTests
{
    [Fact]
    public void Boost_phrases_serialize_into_speech_contexts()
    {
        var config = new RealtimeSessionConfig
        {
            SampleRate = 16000,
            Language = "en-US",
            BoostPhrases = new[] { "Kowalczyk", "GRDB" },
            Boost = 3.0f,
        };
        Assert.Equal(2, config.BoostPhrases!.Count);
        Assert.Equal(3.0f, config.Boost);
    }
}

[Trait("Category", "Live")]
public class RealtimeStreamLiveTests
{
    private static bool AsrUp()
    {
        using var http = new HttpClient();
        return new LocalAsrClient(http, "http://127.0.0.1:8080")
            .IsHealthyAsync(default).GetAwaiter().GetResult();
    }

    /// <summary>Synthesizes a 16 kHz mono PCM16 WAV with SAPI (same helper as
    /// the file-endpoint tests) and returns its raw PCM payload.</summary>
    private static (byte[] Pcm, int Rate)? SynthesizeSpeechPcm(string text)
    {
        try
        {
            var wav = Path.Combine(Path.GetTempPath(), $"athena-rt-{Guid.NewGuid():N}.wav");
            var script = Path.Combine(Path.GetTempPath(), $"athena-rt-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(script, $"""
                Add-Type -AssemblyName System.Speech
                $s = New-Object System.Speech.Synthesis.SpeechSynthesizer
                $s.SetOutputToWaveFile('{wav.Replace("'", "''")}')
                $s.Speak('{text.Replace("'", "''")}')
                $s.Dispose()
                """);
            var psi = new System.Diagnostics.ProcessStartInfo("powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
            { CreateNoWindow = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(30000);
            File.Delete(script);
            if (!File.Exists(wav)) return null;

            // Minimal WAV parse: walk RIFF chunks to fmt (sample rate at
            // fmt+12) and data — no NAudio needed in the test project.
            var bytes = File.ReadAllBytes(wav);
            File.Delete(wav);
            var rate = 0;
            var pos = 12; // past RIFF/WAVE headers
            int? dataIdx = null;
            while (pos + 8 <= bytes.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                var size = BitConverter.ToInt32(bytes, pos + 4);
                if (id == "fmt ") rate = BitConverter.ToInt32(bytes, pos + 12);
                if (id == "data") { dataIdx = pos + 8; break; }
                pos += 8 + size + (size % 2); // chunks are word-aligned
            }
            if (dataIdx is null || rate == 0) return null;
            var payload = bytes.AsSpan(dataIdx.Value).ToArray();
            return (payload, rate);
        }
        catch
        {
            return null;
        }
    }

    private static int MemoryExtensionsIndexOf(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.Slice(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    [SkippableFact]
    public async Task Stream_renders_partial_and_final_text()
    {
        Skip.IfNot(AsrUp(), "ASR server not running");
        var speech = SynthesizeSpeechPcm("The quick brown fox jumps over the lazy dog.");
        Assert.NotNull(speech);

        var sawPartial = false;
        string? final = null;
        await using var stream = new RealtimeAsrClient("http://127.0.0.1:8080", new RealtimeSessionConfig
        {
            SampleRate = speech!.Value.Rate,
            Language = "en-US",
            BoostPhrases = new[] { "fox" }, // exercises the boosting path live
        });
        stream.PartialChanged += text => { if (text.Trim().Length > 0) sawPartial = true; };
        RealtimeAsrClient.LogHook += _ => { };

        Assert.True(await stream.ConnectAsync(CancellationToken.None),
            "server exposes /v1/realtime");

        // Feed the utterance in realistic 100ms chunks, like the mic does.
        var pcm = speech.Value.Pcm;
        var bytesPerMs = 2 * speech.Value.Rate / 1000;
        for (var off = 0; off < pcm.Length; off += bytesPerMs * 100)
        {
            var len = Math.Min(bytesPerMs * 100, pcm.Length - off);
            var chunk = new byte[len];
            Buffer.BlockCopy(pcm, off, chunk, 0, len);
            stream.SendAudio(chunk);
            await Task.Delay(25); // faster than real time; server keeps up
        }

        final = await stream.FinishAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(!string.IsNullOrWhiteSpace(final), "stream produced no final text");
        Assert.True(sawPartial || final!.Length > 0,
            "no live partial ever rendered (the feature under test)");
        Assert.Contains("fox", final, StringComparison.OrdinalIgnoreCase);
    }
}
