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

namespace Jot.Core.Clients;

public sealed class AsrTranscriptionResult
{
    public string Text { get; init; } = "";
}

public interface ITranscriber
{
    Task<string> TranscribeAsync(string wavPath, CancellationToken ct);
}

public sealed class LocalAsrClient : ITranscriber
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _language;
    private readonly Func<IReadOnlyList<string>?>? _boostProvider;

    public LocalAsrClient(HttpClient http, string baseUrl, string? language = null,
        Func<IReadOnlyList<string>?>? boostProvider = null)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _language = string.IsNullOrWhiteSpace(language) ? null : LanguageCatalog.Normalize(language);
        _boostProvider = boostProvider;
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
    {
        await using var file = File.OpenRead(wavPath);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(file);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", Path.GetFileName(wavPath));
        // Whisper-compatible knobs the server ignores gracefully if unsupported.
        content.Add(new StringContent("json"), "response_format");
        if (_language is not null)
            content.Add(new StringContent(_language), "language");
        if (BoostJson is { } boost)
            content.Add(new StringContent(boost), "speech_contexts");

        using var resp = await _http.PostAsync($"{_baseUrl}/v1/audio/transcriptions", content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new TranscriptionException(
                $"ASR server returned {(int)resp.StatusCode}: {Truncate(body)}",
                (int)resp.StatusCode);
        }
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        // Auto-detect mode leaves <xx-XX> tags after terminal punctuation —
        // never let model markup reach the editor or the validation gate.
        return LanguageCatalog.StripLanguageTags(text).Trim();
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
