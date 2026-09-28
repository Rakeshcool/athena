// Windows-port original: client for the local Nemotron ASR server
// (nemotron.cpp / llama.cpp-family server exposing the OpenAI Whisper-compatible
// POST /v1/audio/transcriptions endpoint). Probed live:
//   GET  /health → {"status":"ok"}
//   GET  /v1/models → {"data":[{"id":"nemotron-3.5-asr-streaming-0.6b.q8_0.gguf",...}]}
//   POST /v1/audio/transcriptions (multipart file=@audio.wav) → {"text":"..."}

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Athena.Core.Clients;

public sealed class AsrTranscriptionResult
{
    public string Text { get; init; } = "";
}

public interface ITranscriber
{
    Task<string> TranscribeAsync(string wavPath, CancellationToken ct);

    /// <summary>Transcribe with an explicit BCP-47 language override (per-app
    /// profiles). Empty/null = the client's configured language. Default
    /// interface method forwards to the 2-arg form so implementations and
    /// test fakes that don't care about profiles keep compiling.</summary>
    Task<string> TranscribeAsync(string wavPath, CancellationToken ct, string? language)
        => TranscribeAsync(wavPath, ct);

    /// <summary>Transcribe keeping the server's per-word timings. Default
    /// returns Empty so implementations and test fakes that don't care about
    /// timelines keep compiling; callers treat Empty as plain text with no
    /// words (composition and export fall back to the untimed path).</summary>
    Task<TimedTranscript> TranscribeTimedAsync(string wavPath, CancellationToken ct, string? language)
        => Task.FromResult(TimedTranscript.Empty);
}

public sealed class LocalAsrClient : ITranscriber
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _language;
    private readonly Func<string?>? _languageProvider;
    private readonly Func<IReadOnlyList<string>?>? _boostProvider;

    public LocalAsrClient(HttpClient http, string baseUrl, string? language = null,
        Func<IReadOnlyList<string>?>? boostProvider = null,
        Func<string?>? languageProvider = null)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _language = string.IsNullOrWhiteSpace(language) ? null : LanguageCatalog.Normalize(language);
        _boostProvider = boostProvider;
        _languageProvider = languageProvider;
    }

    /// <summary>The language for THIS request. The provider (when wired) is
    /// consulted per call so a Settings change applies to the retry/fallback
    /// path immediately — a constructor-captured value would keep transcribing
    /// in the language the app STARTED with until restart.</summary>
    private string? EffectiveLanguage
    {
        get
        {
            var live = _languageProvider?.Invoke();
            var code = string.IsNullOrWhiteSpace(live) ? _language : live;
            return string.IsNullOrWhiteSpace(code) ? null : LanguageCatalog.Normalize(code);
        }
    }

    /// <summary>speech_contexts JSON for word boosting — the dictionary terms,
    /// evaluated per request so dictionary edits apply without a restart.</summary>
    private string? BoostJson
    {
        get
        {
            var phrases = _boostProvider?.Invoke();
            if (phrases is not { Count: > 0 }) return null;
            return JsonSerializer.Serialize(new[] { new { phrases, boost = 3.0f } });
        }
    }

    public async Task<string> TranscribeAsync(string wavPath, CancellationToken ct)
        => await TranscribeAsync(wavPath, ct, language: null);

    public async Task<string> TranscribeAsync(string wavPath, CancellationToken ct, string? language)
    {
        var (ok, status, body) = await PostTranscriptionAsync(wavPath, language, "json", ct);
        if (!ok)
            throw new TranscriptionException(
                $"ASR server returned {status}: {Truncate(body)}", status);
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        // Auto-detect mode leaves <xx-XX> tags after terminal punctuation —
        // never let model markup reach the editor or the validation gate.
        return LanguageCatalog.StripLanguageTags(text).Trim();
    }

    /// <summary>Transcribe and keep the model's word timings: requests the
    /// server's verbose_json response format — a native capability of the
    /// Nemotron server (verified live: duration + per-word start/end/confidence),
    /// NOT custom alignment — parsed by TranscriptTimeline. A blank body parses
    /// to Empty; an HTTP failure throws TranscriptionException exactly like the
    /// plain path, so retries and history rows behave the same.</summary>
    public async Task<TimedTranscript> TranscribeTimedAsync(string wavPath, CancellationToken ct, string? language = null)
    {
        var (ok, status, body) = await PostTranscriptionAsync(wavPath, language, "verbose_json", ct);
        if (!ok)
            throw new TranscriptionException(
                $"ASR server returned {status}: {Truncate(body)}", status);
        return TranscriptTimeline.FromVerboseJson(body);
    }

    /// <summary>The shared multipart request. response_format is a server
    /// dropdown: "json" for plain dictation (identical request bytes to
    /// before), "verbose_json" adds duration and per-word timings at roughly
    /// the same latency.</summary>
    private async Task<(bool Ok, int StatusCode, string Body)> PostTranscriptionAsync(
        string wavPath, string? language, string responseFormat, CancellationToken ct)
    {
        await using var file = File.OpenRead(wavPath);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(file);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", Path.GetFileName(wavPath));
        // Whisper-compatible knobs the server ignores gracefully if unsupported.
        content.Add(new StringContent(responseFormat), "response_format");
        // Explicit per-call language (app profile) wins over the configured one.
        var lang = string.IsNullOrWhiteSpace(language)
            ? EffectiveLanguage
            : LanguageCatalog.Normalize(language);
        if (lang is not null)
            content.Add(new StringContent(lang), "language");
        if (BoostJson is { } boost)
            content.Add(new StringContent(boost), "speech_contexts");

        using var resp = await _http.PostAsync($"{_baseUrl}/v1/audio/transcriptions", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];
}

/// <summary>Maps HTTP status codes onto the shared failure taxonomy (F-matrix).</summary>
public sealed class TranscriptionException : Exception
{
    public int? StatusCode { get; }
    public TranscriptionException(string message, int? statusCode = null) : base(message)
        => StatusCode = statusCode;
}
