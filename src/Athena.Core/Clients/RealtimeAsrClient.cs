// Windows-port original: streaming client for the local NeMo ASR server's
// realtime WebSocket transcription protocol, per the official API reference
// (docs/api.md in NVIDIA/NeMo-Speech.cpp) and mapped from the server's own
// web client:
//   ws(s)://host/v1/audio/transcriptions/realtime   (canonical; /v1/realtime
//     is a legacy alias kept for older servers)
//   → server sends session.created on connect
//   → one session.update BEFORE any audio: {sample_rate, language,
//       automatic_punctuation, speech_contexts (word boosting)}
//   → binary frames: raw PCM16 mono little-endian at the declared sample_rate
//   ← conversation.item.input_audio_transcription.delta   live partial words
//   ← conversation.item.input_audio_transcription.completed  final segment
//   → on stop: input_audio_buffer.commit → .committed ack
//   → on cancel: input_audio_buffer.clear (discards buffered audio)
//   ← {"type":"error","error":{"message":…}}
//
// The file endpoint stays the fallback: if the stream produced nothing, the
// coordinator re-transcribes the WAV — never-lose-words, stream or not.

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Athena.Core.Clients;

/// <summary>Pure accumulator for the realtime event stream — the render rule is
/// the server web client's: finals joined by newlines, then the open partial.
/// Unit-testable without a socket.</summary>
public sealed class RealtimeTranscript
{
    private readonly List<string> _finals = new();
    private string _partial = "";

    /// <summary>All finals are final; the trailing partial is provisional.
    /// Finals join with a SPACE: the segments are continuous speech split by
    /// server endpointing, not paragraph breaks — a newline would ride all the
    /// way into the cleaned text and be INSERTED as hard line breaks.</summary>
    public string Render =>
        _finals.Count == 0 ? _partial
        : _partial.Length == 0 ? string.Join(" ", _finals)
        : string.Join(" ", _finals) + " " + _partial;

    public bool HasText => Render.Trim().Length > 0;
    public IReadOnlyList<string> Finals => _finals;

    public void AbsorbDelta(string? delta)
    {
        if (!string.IsNullOrEmpty(delta)) _partial += delta;
    }

    public void AbsorbCompleted(string? transcript)
    {
        if (!string.IsNullOrEmpty(transcript)) _finals.Add(transcript);
        _partial = "";
    }

    /// <summary>Commit-time: any dangling partial becomes a final so it isn't lost.</summary>
    public void SealPartial()
    {
        if (_partial.Trim().Length > 0) _finals.Add(_partial);
        _partial = "";
    }

    public void Reset()
    {
        _finals.Clear();
        _partial = "";
    }
}

/// <summary>Realtime session configuration, per the server's session.update
/// schema. SpeechContexts carries the dictionary terms for ASR-level word
/// boosting ({"phrases": [...], "boost": N}); Language accepts "auto" for
/// model-side language detection.</summary>
public sealed record RealtimeSessionConfig
{
    public int SampleRate { get; init; } = 16000;
    /// <summary>BCP-47 locale or "auto" (server/model-side detection).</summary>
    public string Language { get; init; } = LanguageCatalog.Default;
    public bool AutomaticPunctuation { get; init; } = true;
    /// <summary>Word-boosting phrase groups; the dictionary rides here.</summary>
    public IReadOnlyList<string>? BoostPhrases { get; init; }
    /// <summary>Boost value for BoostPhrases (server scoring default is ~2-4).</summary>
    public float Boost { get; init; } = 3.0f;
}

/// <summary>One live dictation stream. Created per session; SendAudio pushes
/// PCM16 mono chunks as the mic delivers them, FinishAsync commits and drains
/// the finals, CancelAsync discards buffered audio. Every failure degrades to
/// the file path — the stream is an optimization, never the record.</summary>
public sealed class RealtimeAsrClient : IAsyncDisposable
{
    /// <summary>The canonical realtime transcription path (docs/api.md). Older
    /// servers only expose the /v1/realtime alias; connect tries canonical
    /// first and falls back once, remembered for subsequent sessions.</summary>
    private const string CanonicalPath = "/v1/audio/transcriptions/realtime";
    private const string LegacyPath = "/v1/realtime";
    private static volatile string? _knownGoodPath;

    private readonly ClientWebSocket _ws;
    private readonly string _baseUrl;
    private readonly RealtimeSessionConfig _config;
    private readonly RealtimeTranscript _transcript = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private volatile bool _committed;
    private volatile bool _committedAck;
    private volatile bool _dead;
    private Task? _receiveLoop;

    /// <summary>Rendered live text changed (finals + current partial).</summary>
    public event Action<string>? PartialChanged;
    /// <summary>The stream failed; the coordinator should fall back to the file path.</summary>
    public event Action<string>? Failed;

    public RealtimeAsrClient(string baseUrl, RealtimeSessionConfig config)
    {
        _ws = new ClientWebSocket();
        _baseUrl = baseUrl.Trim().TrimEnd('/');
        _config = config;
    }

    public RealtimeAsrClient(string baseUrl, string language, int sampleRate)
        : this(baseUrl, new RealtimeSessionConfig { Language = language, SampleRate = sampleRate })
    {
    }

    public static string ToWsUrl(string httpUrl, string path = CanonicalPath)
    {
        var u = httpUrl.Trim().TrimEnd('/');
        var ws = u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + u[8..]
               : u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + u[7..]
               : "ws://" + u;
        return ws + path;
    }

    /// <summary>Connect + session.update. Returns false (and logs via Failed)
    /// when the server has no realtime endpoint — the caller falls back.</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        // Path selection: known-good wins; otherwise canonical, then legacy.
        var first = _knownGoodPath ?? CanonicalPath;
        var second = first == CanonicalPath ? LegacyPath : CanonicalPath;
        foreach (var path in new[] { first, second })
        {
            if (await TryConnectAsync(ToWsUrl(_baseUrl, path), ct))
            {
                _knownGoodPath = path;
                return true;
            }
            if (_dead) return false; // config/auth failure, not a 404-style miss
        }
        return false;
    }

    private async Task<bool> TryConnectAsync(string url, CancellationToken ct)
    {
        try
        {
            await _ws.ConnectAsync(new Uri(url), ct);

            var boost = _config.BoostPhrases is { Count: > 0 }
                ? JsonSerializer.Serialize(new[]
                  {
                      new { phrases = _config.BoostPhrases, boost = _config.Boost },
                  })
                : null;
            var session = JsonSerializer.Serialize(new
            {
                type = "session.update",
                session = boost is null
                    ? (object)new
                    {
                        sample_rate = _config.SampleRate,
                        language = _config.Language,
                        automatic_punctuation = _config.AutomaticPunctuation,
                    }
                    : new
                    {
                        sample_rate = _config.SampleRate,
                        language = _config.Language,
                        automatic_punctuation = _config.AutomaticPunctuation,
                        speech_contexts = JsonSerializer.Deserialize<JsonElement>(boost),
                    },
            });
            await SendTextAsync(session, ct);
            _receiveLoop = ReceiveLoopAsync(CancellationToken.None); // outlives ct
            return true;
        }
        catch (Exception ex)
        {
            Failed?.Invoke($"realtime connect failed ({url[_baseUrl.Length..]}): {ex.Message}");
            return false;
        }
    }

    /// <summary>Push one PCM16 mono LE chunk from the capture thread. Errors
    /// kill the stream (fallback handles it) — they must never throw into NAudio.</summary>
    public void SendAudio(ReadOnlyMemory<byte> pcm16Mono)
    {
        if (_dead || _committed || _ws.State != WebSocketState.Open) return;
        _ = SendAudioAsync(pcm16Mono);
    }

    private async Task SendAudioAsync(ReadOnlyMemory<byte> data)
    {
        try
        {
            await _sendGate.WaitAsync();
            try
            {
                if (_dead || _committed || _ws.State != WebSocketState.Open) return;
                await _ws.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            finally { _sendGate.Release(); }
        }
        catch (Exception ex)
        {
            KillStream($"realtime send failed: {ex.Message}");
        }
    }

    private async Task SendTextAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally { _sendGate.Release(); }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                using var msg = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        return;
                    }
                    msg.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                HandleEvent(Encoding.UTF8.GetString(msg.ToArray()));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A dead receive loop mid-dictation is not fatal: whatever finals
            // arrived are still in _transcript; the rest falls back to file.
            KillStream($"realtime receive ended: {ex.Message}");
        }
    }

    private void HandleEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (type.EndsWith(".delta"))
            {
                var d = doc.RootElement.TryGetProperty("delta", out var dd) ? dd.GetString() : null;
                _transcript.AbsorbDelta(LanguageCatalog.StripLanguageTags(d ?? ""));
                PartialChanged?.Invoke(_transcript.Render);
            }
            else if (type.EndsWith(".completed"))
            {
                var text = doc.RootElement.TryGetProperty("transcript", out var tr) ? tr.GetString()
                         : doc.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                _transcript.AbsorbCompleted(text is null ? null : LanguageCatalog.StripLanguageTags(text));
                PartialChanged?.Invoke(_transcript.Render);
            }
            else if (type == "error")
            {
                var m = doc.RootElement.TryGetProperty("error", out var e)
                    && e.TryGetProperty("message", out var m2) ? m2.GetString() : null;
                KillStream($"realtime error: {m ?? "unknown"}");
            }
            // input_audio_buffer.committed (ack), session.created/updated: no-ops
            // except the committed ack, which FinishAsync waits on.
            if (type == "input_audio_buffer.committed") _committedAck = true;
        }
        catch (JsonException)
        {
            // Non-JSON frame — ignore; the file fallback covers any gap.
        }
    }

    private void KillStream(string reason)
    {
        if (_dead) return;
        _dead = true;
        FileLogNote(reason);
        Failed?.Invoke(reason);
        try { _ws.Abort(); } catch { }
    }

    /// <summary>App injects its file logger here (Core stays headless).</summary>
    public static Action<string>? LogHook { get; set; }

    private static void FileLogNote(string msg) => LogHook?.Invoke(msg);

    /// <summary>The stream is dead or committed — the pump loop uses this to stop.</summary>
    public bool IsFinished => _dead || _committed;

    /// <summary>Discard everything buffered server-side (Esc cancel) and close.
    /// Fire-and-forget safe: best-effort by contract.</summary>
    public async Task CancelAsync()
    {
        _dead = true;
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                await _sendGate.WaitAsync(200);
                try
                {
                    await _ws.SendAsync(Encoding.UTF8.GetBytes(
                        """{"type":"input_audio_buffer.clear"}"""),
                        WebSocketMessageType.Text, true, CancellationToken.None);
                }
                finally { _sendGate.Release(); }
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }
        catch { /* cancel is best-effort */ }
    }

    /// <summary>Stop sending audio (capture already stopped), commit, and drain
    /// finals until the server acks or the grace period lapses. Returns the
    /// full transcript, or null when the stream never produced text.</summary>
    public async Task<string?> FinishAsync(TimeSpan grace, CancellationToken ct)
    {
        if (_dead || _ws.State != WebSocketState.Open)
            return _transcript.HasText ? _transcript.Render : null;

        try
        {
            // In-flight audio sends first, then the commit marker.
            await _sendGate.WaitAsync(ct);
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.SendAsync(Encoding.UTF8.GetBytes(
                        """{"type":"input_audio_buffer.commit"}"""),
                        WebSocketMessageType.Text, true, ct);
            }
            finally { _sendGate.Release(); }

            _committed = true;

            // Fast path: the server acks the commit right after the last
            // .completed — return as soon as the ack lands (plus a short
            // settle for event-order jitter). Slow path: ride out the grace.
            var deadline = DateTime.UtcNow + grace;
            while (DateTime.UtcNow < deadline)
            {
                if (_committedAck && _transcript.HasText)
                {
                    await Task.Delay(150, ct);
                    break;
                }
                if (_transcript.HasText && _receiveLoop is { IsCompleted: true })
                    break;
                await Task.Delay(25, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            KillStream($"realtime finish failed: {ex.Message}");
        }

        if (_transcript.HasText)
        {
            _transcript.SealPartial(); // promote any dangling partial
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { }
            return _transcript.Render;
        }
        _transcript.SealPartial();
        var sealedText = _transcript.HasText ? _transcript.Render : null;
        if (sealedText is null) KillStream("realtime produced no text");
        return sealedText;
    }

    public ValueTask DisposeAsync()
    {
        _dead = true;
        try { _ws.Abort(); } catch { }
        _ws.Dispose();
        _sendGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
