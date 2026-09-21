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
using System.Threading.Channels;

namespace Athena.Core.Clients;

/// <summary>Pure accumulator for the realtime event stream — the render rule is
/// the server web client's: finals joined by newlines, then the open partial.
/// Unit-testable without a socket.</summary>
public sealed class RealtimeTranscript
{
    // Written by the socket receive loop (.delta/.completed events), read and
    // sealed by FinishAsync on the pipeline thread — two threads, one object.
    // Without this gate a concurrent List.Add corrupts _finals and a torn
    // _partial read can drop the trailing words from the final transcript.
    private readonly object _gate = new();
    private readonly List<string> _finals = new();
    private string _partial = "";

    /// <summary>All finals are final; the trailing partial is provisional.
    /// Finals join with a SPACE: the segments are continuous speech split by
    /// server endpointing, not paragraph breaks — a newline would ride all the
    /// way into the cleaned text and be INSERTED as hard line breaks.</summary>
    public string Render
    {
        get
        {
            lock (_gate)
            {
                return _finals.Count == 0 ? _partial
                    : _partial.Length == 0 ? string.Join(" ", _finals)
                    : string.Join(" ", _finals) + " " + _partial;
            }
        }
    }

    public bool HasText => Render.Trim().Length > 0;

    public void AbsorbDelta(string? delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        lock (_gate) _partial += delta;
    }

    public void AbsorbCompleted(string? transcript)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(transcript)) _finals.Add(transcript);
            _partial = "";
        }
    }

    /// <summary>Commit-time: any dangling partial becomes a final so it isn't lost.</summary>
    public void SealPartial()
    {
        lock (_gate)
        {
            if (_partial.Trim().Length > 0) _finals.Add(_partial);
            _partial = "";
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _finals.Clear();
            _partial = "";
        }
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
    // The web client's contract, mechanically: its audio callback calls
    // socket.send per buffer and the browser delivers frames IN ORDER. The
    // channel + single sender task reproduce that FIFO guarantee. (The
    // previous fire-and-forget SendAudio-per-chunk raced a semaphore between
    // concurrent send tasks — two chunks could swap acquisition order and
    // reach the server as scrambled audio slices: mangled words mid-sentence.)
    private readonly Channel<ReadOnlyMemory<byte>> _outgoing =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
            new UnboundedChannelOptions { SingleReader = true });
    private Task? _senderLoop;
    // The reference client's handshake: it waits for session.updated before
    // the first audio byte — audio never precedes an acknowledged session
    // shape (rate, language).
    private readonly TaskCompletionSource _sessionUpdated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Server-reported decode progress (seconds × 1000; reader thread only).
    private long _audioProcessedTicks;
    // TickCount64 of the last text event (delta fragment or completed final) —
    // the drain loop's liveness signal, written on the reader thread only.
    private long _lastTextEventTicks;

    private volatile bool _committed;
    private volatile bool _committedAck;
    private volatile bool _dead;
    private Task? _receiveLoop;
    /// <summary>Total PCM bytes actually sent to the socket. The no-text
    /// diagnosis needs it: "sent 360 KB, no events" is a server problem;
    /// "sent 0 KB" is a client pump problem. Those are different bugs.</summary>
    private long _bytesSent;

    /// <summary>Track whether any text event has arrived at all. The
    /// "straight to done" diagnosis needs it: with the server's decode lag,
    /// a short dictation can release the key before the FIRST delta lands —
    /// no live text ever shown, though the session was perfectly healthy.
    /// Written by the receive loop, read by the pipeline thread — volatile,
    /// or a cached false hides real text from the drain's liveness checks.</summary>
    public volatile bool HasEmittedText;

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
            _receiveLoop = ReceiveLoopAsync(CancellationToken.None); // reader up FIRST: it consumes the handshake

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
            // The reference client's handshake, matched: the server sends
            // session.created on connect; the client sends session.update and
            // WAITS for session.updated before audio flows. (We previously
            // fired session.update and streamed immediately — the config an
            // ASR stream needs was never acknowledged.)
            await SendTextAsync(session, ct);
            await WaitEventAsync("session.updated", TimeSpan.FromSeconds(15));

            _senderLoop = SenderLoopAsync(); // acked session first, then audio
            return true;
        }
        catch (Exception ex)
        {
            try { _ws.Abort(); } catch { } // a failed path attempt must not poison the retry
            var msg = $"realtime connect failed ({url[_baseUrl.Length..]}): {ex.Message}";
            FileLogNote(msg);
            Failed?.Invoke(msg);
            return false;
        }
    }

    /// <summary>Wait for the reader to observe the session.updated ACK —
    /// the gate the reference client holds audio behind. session.created is
    /// informational (server hello) and not gated on. Timeout ⇒ connect
    /// fails ⇒ path retry/file fallback, never a silently misconfigured
    /// stream.</summary>
    private async Task WaitEventAsync(string name, TimeSpan timeout)
    {
        var done = await Task.WhenAny(_sessionUpdated.Task, Task.Delay(timeout));
        if (done != _sessionUpdated.Task)
            throw new TimeoutException($"realtime handshake: no {name} within {timeout.TotalSeconds:F0}s");
        if (_dead)
            throw new InvalidOperationException("realtime stream died during handshake");
    }

    /// <summary>Push one PCM16 mono LE chunk from the capture thread. Chunks
    /// are queued in arrival order; the single sender task delivers them FIFO
    /// to the socket. Errors kill the stream (fallback handles it) — they
    /// must never throw into NAudio.</summary>
    public void SendAudio(ReadOnlyMemory<byte> pcm16Mono)
    {
        if (_dead || _committed) return;
        _outgoing.Writer.TryWrite(pcm16Mono);
    }

    /// <summary>Uniform-frame cadence: the reference client streams 100ms
    /// chunks (CHUNK_MS_DEFAULT). WASAPI hands us ~10ms slivers; the sender
    /// re-frames them into ~100ms wire frames so the server sees exactly what
    /// its proven clients send. Byte order is preserved exactly.</summary>
    private const int ChunkMs = 100;

    /// <summary>The socket's one writer: channel order = wire order. Exits
    /// when the channel is completed and drained (finish) or on the first
    /// dead/commit/closed signal.</summary>
    private async Task SenderLoopAsync()
    {
        var frameBytes = Math.Max(2, (int)((long)_config.SampleRate * ChunkMs / 1000) * 2);
        var carry = Array.Empty<byte>();
        try
        {
            while (await _outgoing.Reader.WaitToReadAsync())
                while (_outgoing.Reader.TryRead(out var chunk))
                {
                    if (_dead || _committed) return;
                    if (_ws.State != WebSocketState.Open)
                    {
                        KillStream($"realtime send aborted (socket {_ws.State})");
                        return;
                    }
                    // Accumulate slivers; emit every whole ~100ms frame.
                    var combined = new byte[carry.Length + chunk.Length];
                    Array.Copy(carry, combined, carry.Length);
                    chunk.Span.CopyTo(combined.AsSpan(carry.Length));
                    var whole = combined.Length - combined.Length % frameBytes;
                    if (whole > 0)
                    {
                        await _ws.SendAsync(new ArraySegment<byte>(combined, 0, whole),
                            WebSocketMessageType.Binary, true, CancellationToken.None);
                        Interlocked.Add(ref _bytesSent, whole);
                    }
                    carry = combined.AsSpan(whole).ToArray();
                }
            // Channel completed (finish): flush the trailing partial frame.
            if (carry.Length > 0 && !_dead && !_committed && _ws.State == WebSocketState.Open)
            {
                await _ws.SendAsync(carry, WebSocketMessageType.Binary, true, CancellationToken.None);
                Interlocked.Add(ref _bytesSent, carry.Length);
            }
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
                // Spacing-preserving strip: deltas are fragments that carry
                // their own leading spaces (" is"); trimming here glued the
                // live words together while .completed finals stayed correct.
                var fragment = LanguageCatalog.StripLanguageTagsPreserveSpacing(d);
                if (fragment.Length > 0)
                {
                    HasEmittedText = true;
                    Interlocked.Exchange(ref _lastTextEventTicks, Environment.TickCount64);
                    _transcript.AbsorbDelta(fragment);
                }
                // The reference client reads audio_processed off the delta
                // events: how many seconds the server has actually decoded.
                if (doc.RootElement.TryGetProperty("audio_processed", out var ap)
                    && ap.TryGetDouble(out var secs))
                    Interlocked.Exchange(ref _audioProcessedTicks, (long)(secs * 1000));
                PartialChanged?.Invoke(_transcript.Render);
            }
            else if (type.EndsWith(".completed"))
            {
                var text = doc.RootElement.TryGetProperty("transcript", out var tr) ? tr.GetString()
                         : doc.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                var final = text is null ? null : LanguageCatalog.StripLanguageTags(text);
                if (!string.IsNullOrWhiteSpace(final))
                {
                    HasEmittedText = true;
                    Interlocked.Exchange(ref _lastTextEventTicks, Environment.TickCount64);
                }
                _transcript.AbsorbCompleted(final);
                PartialChanged?.Invoke(_transcript.Render);
            }
            else if (type == "error")
            {
                var m = doc.RootElement.TryGetProperty("error", out var e)
                    && e.TryGetProperty("message", out var m2) ? m2.GetString() : null;
                KillStream($"realtime error: {m ?? "unknown"}");
            }
            // session.updated ACKs the config (handshake gate); the committed
            // ack is what FinishAsync waits on.
            if (type == "input_audio_buffer.committed") _committedAck = true;
            else if (type == "session.updated") _sessionUpdated.TrySetResult();
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

    /// <summary>The pump loop uses this to stop. Commit alone must NOT finish
    /// the pump: after input_audio_buffer.commit the server still needs the
    /// residual queued audio — and on the coordinator side the pump is what
    /// feeds trailing-capture audio (key-up while still speaking). Exiting at
    /// commit stranded every slice after finalize, so the streamed transcript
    /// silently lost the trailing words the mic kept hearing. Keep sending
    /// until the ack (server done) or death.</summary>
    public bool IsFinished => _dead || (_committed && _committedAck);

    /// <summary>Socket usable — the pump's other exit condition (covers the
    /// no-ack grace path, where FinishAsync closes without an ack).</summary>
    public bool IsOpen => _ws.State == WebSocketState.Open;

    /// <summary>The sample rate declared in session.update (the capture's
    /// native rate — the web client's contract; logging/diagnosis only).</summary>
    public int DeclaredSampleRate => _config.SampleRate;

    /// <summary>Discard everything buffered server-side (Esc cancel) and close.
    /// Fire-and-forget safe: best-effort by contract.</summary>
    public async Task CancelAsync()
    {
        _dead = true;
        _outgoing.Writer.TryComplete();
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

    /// <summary>Drain the server's DECODE LAG at key-up (display-only mode).
    /// The stream stops receiving audio, but the server keeps decoding what
    /// it already holds and emits deltas/completed with a lag — on a short
    /// dictation that lag exceeds the remaining speech, so killing the socket
    /// at key-up meant the live text NEVER showed (pill jumped to "done").
    /// Waits until no new text event has landed for QuietMs, the socket dies,
    /// or capMs elapses. Cancel/Dispose stay safe mid-drain (listeners were
    /// detached first; SendAudio no-ops once _dead flips).</summary>
    /// <summary>Drain the server's DECODE LAG at key-up (display-only mode).
    /// The previous version called CancelAsync first — which sets _dead and
    /// CLOSES the socket — so this loop's `IsOpen && !_dead` guard exited on
    /// its first check and nothing was ever drained. The correct order: stop
    /// the audio supply, keep the socket OPEN and the receive loop RUNNING
    /// while the lagging deltas/completed land, and only then cancel+close.
    /// Waits until no text event for quietMs, the socket dies, or capMs.
    /// (Sending no commit marker: the drain collects only what the server
    /// was already emitting; input_audio_buffer.clear at the end discards
    /// whatever was still buffered undecoded.)</summary>
    public async Task DrainDisplayAsync(int quietMs = 450, int capMs = 3000)
    {
        // Stop the sender (audio supply ended with the capture); the socket
        // and receive loop stay alive so in-flight text events still land.
        _outgoing.Writer.TryComplete();
        // Seed the quiet-clock when NOTHING has arrived yet: _lastTextEventTicks
        // starts at 0, and TickCount64 - 0 < quietMs is never true — the drain
        // used to exit instantly for the exact case it exists for (a short take
        // released before the server's first delta landed).
        Interlocked.CompareExchange(ref _lastTextEventTicks, Environment.TickCount64, 0);
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < capMs
               && Environment.TickCount64 - Interlocked.Read(ref _lastTextEventTicks) < quietMs
               && IsOpen && !_dead)
        {
            await Task.Delay(60).ConfigureAwait(false);
        }
        await CancelAsync().ConfigureAwait(false); // discard undecoded buffer, close
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
            // The web client's order, mechanically: all queued audio bytes
            // cross the wire BEFORE the commit marker. (The earlier version
            // committed synchronously while the queue still held unsent
            // trailing-capture audio — those words never reached the server.)
            if (_senderLoop is not null)
            {
                _outgoing.Writer.TryComplete();
                await _senderLoop; // every queued chunk sent, then the loop exits
            }

            if (_ws.State != WebSocketState.Open)
            {
                KillStream($"realtime socket died before commit (sent {Interlocked.Read(ref _bytesSent) / 1024.0:F0} KB)");
                return _transcript.HasText ? _transcript.Render : null;
            }

            // Queue empty and socket open — commit.
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
                // The server closed on us: the transcript's state is final —
                // waiting longer cannot add words (the reference's wait_final
                // surfaces socket death the same way).
                if (_ws.State != WebSocketState.Open)
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
        if (sealedText is null)
            KillStream($"realtime produced no text (sent {Interlocked.Read(ref _bytesSent) / 1024.0:F0} KB, " +
                       $"socket {_ws.State}, commit ack {_committedAck}, " +
                       $"server decoded {Interlocked.Read(ref _audioProcessedTicks) / 1000.0:F1}s)");
        return sealedText;
    }

    public ValueTask DisposeAsync()
    {
        _dead = true;
        _outgoing.Writer.TryComplete();
        _sessionUpdated.TrySetCanceled(); // unblock a connect waiting for the ack
        try { _ws.Abort(); } catch { }
        _ws.Dispose();
        _sendGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
