// Windows-port original: client for the local llama.cpp llama-server
// (OpenAI-compatible POST /v1/chat/completions). Probed live on :3000:
//   GET  /health → {"status":"ok"}
//   POST /v1/chat/completions → {"choices":[{"message":{"content":..., "reasoning_content":...}}]}
//
// The served model is a REASONING model (LFM2.5-2.6B): it emits
// reasoning_content separately and can also inline <think>…</think> in content,
// so the client strips think-blocks and prefers the reasoning channel's final
// answer when content is empty.

using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Athena.Core.Clients;

public sealed class LlmChatResult
{
    public string Content { get; init; } = "";
    public string? ReasoningContent { get; init; }
    public string FinishReason { get; init; } = "";
}

public interface ICleaner
{
    /// <summary>Chat-structured cleanup: system carries rules, user the raw
    /// transcript, and the assistant turn is prefilled to suppress reasoning
    /// tokens (see PromptV1.CleanupSystemPrompt).</summary>
    Task<string> CleanAsync(string systemPrompt, string userPrompt, string assistantPrefill, CancellationToken ct);
}

public sealed class LocalLlmClient : ICleaner
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;

    public LocalLlmClient(HttpClient http, string baseUrl, string? model = null)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model ?? "";
    }

    public async Task<LlmChatResult> ChatAsync(
        string systemPrompt, string userPrompt, double temperature, int maxTokens, CancellationToken ct,
        string? assistantPrefill = null)
    {
        var messages = new List<object>
        {
            new Dictionary<string, string> { ["role"] = "system", ["content"] = systemPrompt },
            new Dictionary<string, string> { ["role"] = "user", ["content"] = userPrompt },
        };
        if (assistantPrefill is not null)
            messages.Add(new Dictionary<string, string> { ["role"] = "assistant", ["content"] = assistantPrefill });

        var payload = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
            ["stream"] = false,
        };
        if (_model.Length > 0) payload["model"] = _model;

        using var resp = await _http.PostAsync(
            $"{_baseUrl}/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new TranscriptionException(
                $"LLM server returned {(int)resp.StatusCode}: {Truncate(body)}",
                (int)resp.StatusCode);
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var choice = doc.RootElement.GetProperty("choices")[0];
        var msg = choice.GetProperty("message");
        var content = msg.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var reasoning = msg.TryGetProperty("reasoning_content", out var r) ? r.GetString() : null;
        var finish = choice.TryGetProperty("finish_reason", out var f) ? f.GetString() ?? "" : "";

        var final = ExtractFinalAnswer(content, reasoning);
        return new LlmChatResult { Content = final, ReasoningContent = reasoning, FinishReason = finish };
    }

    public async Task<string> CleanAsync(
        string systemPrompt, string userPrompt, string assistantPrefill, CancellationToken ct)
    {
        var result = await ChatAsync(
            systemPrompt: systemPrompt,
            userPrompt: userPrompt,
            temperature: 0.0,
            maxTokens: 1024,
            ct: ct,
            assistantPrefill: assistantPrefill);
        return result.Content.Trim();
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

    /// <summary>
    /// Reasoning models put the answer in different places depending on the
    /// template. Resolution order:
    ///   1. content with &lt;think&gt;…&lt;/think&gt; blocks removed
    ///   2. content after an unclosed &lt;think&gt; prefix (truncated thinking)
    ///   3. non-empty content as-is
    ///   4. reasoning_content (some templates put ONLY the answer there)
    /// </summary>
    public static string ExtractFinalAnswer(string content, string? reasoning)
    {
        var c = content ?? "";
        // Closed think blocks: drop them.
        if (c.Contains("</think>"))
        {
            var idx = c.LastIndexOf("</think>");
            c = c[(idx + "</think>".Length)..].Trim();
        }
        else if (c.TrimStart().StartsWith("<think>"))
        {
            // Unclosed think block: everything after the tag was thinking; the
            // answer may live in reasoning_content instead.
            c = "";
        }
        if (c.Length > 0) return c.Trim();

        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            // The reasoning channel may itself end with the polished answer; take
            // the last non-empty line as the best guess (validated live: the
            // heretic template ends its reasoning with the cleaned line).
            var lines = reasoning.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length > 0) return lines[^1].Trim('"', '.', ' ');
        }
        return "";
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];
}
