// Windows-port original: the orchestrator, mirroring DictationCoordinator.swift.
// Translates hotkey intents into session lifecycle via the pure
// DictationStateMachine, drives capture → ASR → LLM cleanup → insertion, and
// persists every status transition so nothing is ever lost.
//
// v1.x upgrades ported from the macOS coordinator:
//  - Session CONTEXT is captured per in-flight task, so a new dictation may
//    begin while the previous one is still transcribing/inserting (overlapping
//    sessions; a superseded task's state writes are dropped, its History row
//    still completes).
//  - Trailing capture: key-up while still speaking keeps the mic open until
//    you actually stop (0.25s quiet, 1.5s cap), with the SNR-gated threshold.
//  - NoiseFloorEstimator always runs and records numbers into the row; the
//    honest-silence rule needs BOTH quiet peak and no separation from the room.
//  - Retention pruning: terminal sessions older than the retention window lose
//    their audio; nothing is ever deleted before a transcript exists.

using System.Collections.Concurrent;
using System.IO;
using Athena.App.Audio;
using Athena.App.Insertion;
using Athena.Core;
using Athena.Core.Clients;

namespace Athena.App;

/// <summary>Hotkey intents emitted by the grammar, consumed by the coordinator.</summary>
public enum Intent
{
    Begin,
    LockIn,
    Finalize,
    Cancel,
    ShortTapHint,
    AbortAccidental,
}

/// <summary>Everything an in-flight pipeline task needs to finish without
/// touching state that may already belong to the NEXT session.</summary>
internal sealed record FlightContext(Guid SessionId, DateTime StartedAt, string? TargetApp,
    WavRecorder Recorder, RealtimeAsrClient? Stream, NoiseFloorEstimator Noise);

public sealed class DictationCoordinator : IDisposable
{
    private readonly Func<double> _clock;
    private readonly Func<WavRecorder> _recorderFactory;
    private readonly ITranscriber _transcriber;
    private readonly FormattingPipeline _pipeline;
    private readonly IInserter _inserter;
    private readonly HistoryStore _history;
    private readonly RetryQueueStore _retryQueue;
    private readonly AthenaSettings _settings;
    private readonly DictionaryStore? _dictionary;
    private readonly Action<string> _log;

    private HotkeyProcessor _grammar;
    private WavRecorder? _recorder;
    private DictationState _state = DictationState.Idle;
    private Guid _sessionId;
    private DateTime _startedAt;
    private string? _targetApp;
    private float _latestLevel;
    private NoiseFloorEstimator _noise = new();
    private Task? _inFlight;
    private RealtimeAsrClient? _stream;
    /// <summary>Sessions the user cancelled while already in the pipeline
    /// (Finalizing/Transcribing/Inserting). Written from the UI thread (Esc),
    /// consumed on pipeline background threads — hence concurrent.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _cancelRequested = new();

    /// <summary>Live partial transcript from the realtime stream (audio thread).
    /// The HUD shows it as you speak — the text is provisional until Done.</summary>
    public event Action<string>? LivePartial;

    /// <summary>Grace the commit gets to flush its finals before the file
    /// endpoint takes over (localhost: finals land in tens of ms).</summary>
    private static readonly TimeSpan StreamFinishGrace = TimeSpan.FromMilliseconds(2500);

    /// <summary>Raised when the session locks hands-free (HUD label switch).</summary>
    public event Action? Locked;

    public event Action<DictationState>? StateChanged;
    public event Action<float>? Level;
    public event Action<string>? Hint;
    public event Action<string>? Error;
    /// <summary>Raised when a queued/recovered dictation produced text — the tray shows a balloon.</summary>
    public event Action<string>? Recovered;

    /// <summary>A finished dictation whose cleanup visibly removed words —
    /// (raw, cleaned). The HUD shows the edit, not just the result: cuts are
    /// struck out and collapse, so the cleanup is legible as an action.
    /// Fired after the Done transition so UI handlers run in that order.</summary>
    public event Action<string, string>? CorrectionReady;

    public DictationCoordinator(
        Func<WavRecorder> recorderFactory,
        ITranscriber transcriber,
        FormattingPipeline pipeline,
        IInserter inserter,
        HistoryStore history,
        RetryQueueStore retryQueue,
        AthenaSettings settings,
        Action<string>? log = null,
        DictionaryStore? dictionary = null)
    {
        _clock = () => WavRecorder.Now;
        _recorderFactory = recorderFactory;
        _transcriber = transcriber;
        _pipeline = pipeline;
        _inserter = inserter;
        _history = history;
        _retryQueue = retryQueue;
        _settings = settings;
        _dictionary = dictionary;
        _log = log ?? (_ => { });
        _grammar = new HotkeyProcessor { DoubleTapLockEnabled = false };
    }

    public DictationState State => _state;

    /// <summary>Hotkey events from the UI thread (keyboard hook). The grammar is
    /// pure; the coordinator applies its effects.</summary>
    public void OnHotkeyDown()
    {
        ApplyGrammar(HotkeyEvent.HotkeyDown);
    }

    public void OnHotkeyUp() => ApplyGrammar(HotkeyEvent.HotkeyUp);
    public void OnEscDown()
    {
        // The grammar returns to Idle at finalize, so an Esc during processing
        // would die there unnoticed — route it straight to Cancel, which gates
        // on the processing states itself.
        if (_state is DictationState.Finalizing or DictationState.Transcribing or DictationState.Inserting)
        {
            Cancel();
            return;
        }
        ApplyGrammar(HotkeyEvent.EscDown);
    }

    private void ApplyGrammar(HotkeyEvent ev)
    {
        var fx = _grammar.Handle(ev, _clock());
        foreach (var hi in fx.Intents) HandleIntent(Map(hi));
    }

    private static Intent Map(HotkeyIntent hi) => hi switch
    {
        HotkeyIntent.Begin => Intent.Begin,
        HotkeyIntent.LockIn => Intent.LockIn,
        HotkeyIntent.Finalize => Intent.Finalize,
        HotkeyIntent.Cancel => Intent.Cancel,
        HotkeyIntent.ShortTapHint => Intent.ShortTapHint,
        HotkeyIntent.AbortAccidental => Intent.AbortAccidental,
        _ => Intent.ShortTapHint,
    };

    public void HandleIntent(Intent intent)
    {
        switch (intent)
        {
            case Intent.Begin: Begin(); break;
            case Intent.LockIn: LockIn(); break;
            case Intent.Finalize: FinalizeSession(); break;
            case Intent.Cancel: Cancel(); break;
            case Intent.ShortTapHint:
                // A tap is a coaching moment AND an orphan session: the grammar
                // went idle but the session is still recording — without this
                // cancel the mic runs until Esc and the next key-down is
                // swallowed by the busy guard. Cancel + discard, then coach.
                Cancel(discardArtifacts: true);
                Hint?.Invoke($"Hold {Interop.HotkeyName.For((ushort)_settings.HotkeyVk)} to talk. Hold + Space locks hands-free. Esc cancels.");
                break;
            case Intent.AbortAccidental: Cancel(); break;
        }
    }



    private void Begin()
    {
        // Overlap rule: a NEW dictation may start while the old one is in
        // finalizing/transcribing/inserting — never while actually recording.
        var overlapAllowed = _state is DictationState.Finalizing or DictationState.Transcribing
            or DictationState.Inserting or DictationState.Done or DictationState.Failed
            or DictationState.Cancelled;
        if (_state != DictationState.Idle && !overlapAllowed)
        {
            _grammar.Reset();
            return;
        }

        _sessionId = Guid.NewGuid();
        _startedAt = DateTime.Now;
        _targetApp = Interop.Native.ForegroundProcessName();
        _noise = new NoiseFloorEstimator();
        _latestLevel = 0;
        var sessionId = _sessionId;         // captured: handlers stay per-session
        SetState(DictationState.Warming);

        try
        {
            var folder = SessionFolder(_sessionId);
            Directory.CreateDirectory(folder);
            // capture.wav, NOT audio.wav: StopAsync transcodes the raw capture
            // to audio.wav. The same name for both made the transcode write
            // the file it was reading — it failed on every session (silently,
            // by design) and every History row kept a 48kHz float capture.
            var path = Path.Combine(folder, "capture.wav");
            _recorder = _recorderFactory();
            // Per-session capture, NOT _field handlers: a superseded recorder's
            // late events must never write the new session's noise/level state
            // or fail its row (the bug was FailCurrent using _sessionId).
            _recorder.Level += l =>
            {
                if (sessionId != _sessionId) return; // superseded session's tap
                _latestLevel = l;
                _noise.Ingest(l);
                Level?.Invoke(l);
            };
            _recorder.Failed += ex =>
            {
                if (sessionId != _sessionId) return; // stale recorder failure
                _log($"capture failed: {ex.Message}");
                FileLog.Write($"capture failed: {ex}");
                FailCurrent(DictationFailure.Audio, ex.Message);
            };

            // The realtime stream is created and subscribed BEFORE Start —
            // the web client's exact ordering (socket + session.update exist
            // before getUserMedia's first buffer). No queue, no pump: the
            // audio callback feeds the client's FIFO channel directly, the
            // single sender task is the socket's only writer. (The queue+
            // 20ms-pump design both stranded post-key-up audio in the queue
            // and raced concurrent SendAsync calls — a semaphore let two
            // chunks swap wire order, scrambling the audio.)
            if (_settings.StreamingEnabled)
            {
                var dict = _dictionary?.Snapshot();
                var phrases = dict is null ? null : dict.Terms.Select(t => t.Term).ToList();
                var stream = new RealtimeAsrClient(_settings.AsrBaseUrl, new RealtimeSessionConfig
                {
                    SampleRate = _recorder.NativeSampleRate ?? 48000,
                    Language = _settings.Language,
                    BoostPhrases = phrases is { Count: > 0 } ? phrases : null,
                });
                stream.PartialChanged += text =>
                {
                    // Superseded session: never paint a dying stream's text
                    // over fresh state. (_stream null is fine during finalize —
                    // the display drain keeps delivering late deltas, and that
                    // late live text is exactly what the user should see.)
                    if (sessionId != _sessionId) return;
                    LivePartial?.Invoke(text);
                };
                _stream = stream;
            }

            // Audio callback → FIFO channel. Unconditional: SendAudio no-ops
            // once the stream is dead/committed, so a session that fell back
            // to file transcription just discards.
            if (_stream is not null)
                _recorder.PcmChunk += chunk => _stream?.SendAudio(chunk);
            _recorder.Start(path);
        }
        catch (Exception ex)
        {
            FileLog.Write($"mic open failed: {ex}");
            _recorder = null;
            try { Directory.Delete(SessionFolder(_sessionId), true); } catch { }
            FailCurrent(DictationFailure.NoMicrophone, ex.Message);
            return;
        }

        SetState(DictationState.Recording);

        if (_stream is { } liveStream)
            _ = ConnectStreamAsync(liveStream);

        _history.Upsert(new DictationRecord
        {
            Id = _sessionId,
            StartedAt = _startedAt,
            Status = SessionStatus.Recording,
            TargetAppName = _targetApp,
        });
    }

    /// <summary>Connect the realtime socket. Audio is NOT pumped here: the
    /// capture callback feeds the client's FIFO channel directly (the web
    /// client's mechanism — every byte is queued in arrival order and one
    /// sender task writes the socket, so wire order = capture order). This
    /// method only establishes the session and reports the outcome.</summary>
    private async Task ConnectStreamAsync(RealtimeAsrClient stream)
    {
        try
        {
            if (!await stream.ConnectAsync(CancellationToken.None))
            {
                await stream.DisposeAsync();
                if (_stream == stream) _stream = null;
                return;
            }
            FileLog.Write($"streaming session open (declared {stream.DeclaredSampleRate} Hz)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"stream pump ended: {ex.Message}");
        }
    }

    private void LockIn()
    {
        if (_state == DictationState.Recording)
            Locked?.Invoke();
    }

    private void FinalizeSession()
    {
        if (_state is not (DictationState.Recording or DictationState.Warming)) return;
        var recorder = _recorder;
        if (recorder is null) return;

        SetState(DictationState.Finalizing);
        var flight = new FlightContext(_sessionId, _startedAt, _targetApp, recorder, _stream, _noise);
        _recorder = null;
        _stream = null;

        // Trailing capture: if the user is STILL SPEAKING at key-up, keep the
        // mic open until they stop (hand anticipates mouth). Bounded so a noisy
        // room can never hold the session open.
        var threshold = TrailingCapturePolicy.ThresholdForSession(_noise);
        var wasSpeaking = _latestLevel >= threshold;
        _latestLevel = 0;

        _inFlight = Task.Run(async () =>
        {
            try
            {
                if (wasSpeaking)
                    await CaptureTrailingSpeechAsync(flight.Recorder, threshold);
                var wavPath = await flight.Recorder.StopAsync();
                var streamed = (string?)null;
                if (flight.Stream is not null)
                {
                    // Policy decides whether the stream's final is worth
                    // collecting: with file-fallback off the stream is DISPLAY
                    // only — its text will never be inserted, so don't make
                    // the user wait for the commit round-trip.
                    if (TranscriptSourcePolicy.FinalSource(_settings.StreamingEnabled, _settings.FileFallbackEnabled)
                        == TranscriptSource.LiveStream)
                    {
                        // Web-client ordering lives inside FinishAsync: the FIFO
                        // channel is completed and fully drained BEFORE the commit
                        // marker goes out — trailing-capture audio included.
                        streamed = await flight.Stream.FinishAsync(StreamFinishGrace, CancellationToken.None);
                        FileLog.Write(streamed is not null
                            ? "streamed transcript used (file fallback skipped)"
                            : "stream produced no text — falling back to file transcription");
                    }
                    else
                    {
                        // Display-only stream. Kill the socket's audio supply
                        // immediately, but DRAIN the decode lag before disposing:
                        // the server keeps decoding what it already holds and
                        // emits deltas/completed with a lag — a short dictation
                        // released before the FIRST delta landed would otherwise
                        // show no live text at all (pill jumped straight to
                        // "done"). No commit marker: the drain only collects
                        // what the lag was already about to deliver. The
                        // pipeline runs IN PARALLEL — the drain never delays
                        // the paste.
                        await flight.Stream.CancelAsync();
                        var drain = flight.Stream.DrainAsync();
                        var pipeline = RunPipelineAsync(flight, wavPath, streamed);
                        await Task.WhenAll(drain, pipeline);
                        await flight.Stream.DisposeAsync();
                        FileLog.Write($"display stream drained (text seen: {flight.Stream.HasEmittedText})");
                        return;
                    }
                    await flight.Stream.DisposeAsync();
                }
                await RunPipelineAsync(flight, wavPath, streamed);
            }
            catch (Exception ex)
            {
                // The pipeline must never take the app down mid-dictation.
                FileLog.Write($"PIPELINE FAULT: {ex}");
                try { await flight.Recorder.StopAsync(); } catch { }
                FailFlight(flight, DictationFailure.Network, ex.Message);
            }
            finally
            {
                if (flight.Stream is not null) await flight.Stream.DisposeAsync();
            }
        });
    }

    /// <summary>Keep the mic open past key-up until the user is quiet for
    /// QuietToStopSeconds — capped. Costs nothing in the common case.</summary>
    private static async Task CaptureTrailingSpeechAsync(WavRecorder recorder, float threshold)
    {
        var start = Environment.TickCount64;
        var lastSpeech = start;
        while (Environment.TickCount64 - start < TrailingCapturePolicy.CapSeconds * 1000)
        {
            if (recorder.CurrentLevel >= threshold) lastSpeech = Environment.TickCount64;
            if (Environment.TickCount64 - lastSpeech >= TrailingCapturePolicy.QuietToStopSeconds * 1000)
                return;
            await Task.Delay(40);
        }
    }

    private async Task RunPipelineAsync(FlightContext flight, string? wavPath, string? streamedRaw = null)
    {
        if (wavPath is null || !File.Exists(wavPath))
        {
            FailFlight(flight, DictationFailure.NoAudio, "no audio file was written");
            return;
        }

        // Esc'd mid-processing: stop before spending cleanup/insertion work.
        // The row goes Cancelled with its audio kept — consistent with a
        // recording-phase cancel (the words were wanted once, then refused).
        if (_cancelRequested.TryRemove(flight.SessionId, out _))
        {
            var cancelledDur = WavRecorder.DurationOf(wavPath);
            _history.Upsert(new DictationRecord
            {
                Id = flight.SessionId, StartedAt = flight.StartedAt,
                Status = SessionStatus.Cancelled, AudioPath = wavPath,
                AudioDurationSeconds = cancelledDur.TotalSeconds, TargetAppName = flight.TargetApp,
            });
            SetStateIfCurrent(flight, DictationState.Cancelled);
            return;
        }

        var duration = WavRecorder.DurationOf(wavPath);
        var hasStreamedText = !string.IsNullOrWhiteSpace(streamedRaw);

        // F9b: a very short clip is an accidental blip — silence, not an error.
        // (Streaming text proves it was not a blip regardless of duration.)
        if (!hasStreamedText && duration < TimeSpan.FromMilliseconds(400))
        {
            _history.Upsert(new DictationRecord
            {
                Id = flight.SessionId, StartedAt = flight.StartedAt,
                Status = SessionStatus.Silent, AudioPath = wavPath,
                AudioDurationSeconds = duration.TotalSeconds,
                TargetAppName = flight.TargetApp,
            });
            return;
        }

        _history.Upsert(new DictationRecord
        {
            Id = flight.SessionId, StartedAt = flight.StartedAt,
            Status = SessionStatus.Transcribing, AudioPath = wavPath,
            AudioDurationSeconds = duration.TotalSeconds, TargetAppName = flight.TargetApp,
        });

        // The stream produced a transcript AND policy says the stream is the
        // final source (streaming + file-fallback both on): skip the file
        // endpoint entirely (zero extra latency). Otherwise — fallback off,
        // streaming off, or a stream that never produced text — transcribe
        // the WAV: the whole-utterance decode is the final source, and the
        // stream's failure never costs the words (the file is always there).
        if (hasStreamedText &&
            TranscriptSourcePolicy.FinalSource(_settings.StreamingEnabled, _settings.FileFallbackEnabled)
                == TranscriptSource.LiveStream)
        {
            var final = streamedRaw!;
            if (_settings.CrossCheckAsr)
            {
                // Optional second opinion: the file decode sees the whole
                // utterance at once, the stream decided its first word with
                // zero left context. Only a disagreement at the FIRST word
                // changes anything (TranscriptArbiter.Pick); a failed file
                // decode just keeps the stream text — never costs words.
                try
                {
                    using var cts = new CancellationTokenSource(TimeoutPolicy.OverallDeadline(duration));
                    var fromFile = await _transcriber.TranscribeAsync(wavPath, cts.Token);
                    var arbitrated = TranscriptArbiter.Pick(final, fromFile);
                    if (!string.Equals(arbitrated, final, StringComparison.Ordinal))
                        _log("cross-check: file decode corrected the stream opening");
                    final = arbitrated;
                }
                catch (Exception ex)
                {
                    _log($"cross-check skipped, keeping stream text: {ex.Message}");
                }
            }
            await FinishWithTranscriptAsync(flight, wavPath, duration, final);
            return;
        }

        string raw;
        try
        {
            using var cts = new CancellationTokenSource(TimeoutPolicy.OverallDeadline(duration));
            raw = await _transcriber.TranscribeAsync(wavPath, cts.Token);
        }
        catch (Exception ex)
        {
            await HandleTranscribeFailureAsync(flight, wavPath, duration, Classify(ex), ex.Message);
            return;
        }

        await FinishWithTranscriptAsync(flight, wavPath, duration, raw);
    }

    /// <summary>Shared tail: honest-silence check → cleanup → insertion.</summary>
    private async Task FinishWithTranscriptAsync(
        FlightContext flight, string wavPath, TimeSpan duration, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Honest silence: empty transcript + (quiet peak OR no separation)
            // = nobody spoke → keep quietly. A LOUD recording with empty text
            // is kept too (the judgement could be wrong) — never an error row.
            _history.Upsert(new DictationRecord
            {
                Id = flight.SessionId, StartedAt = flight.StartedAt,
                Status = SessionStatus.Silent, AudioPath = wavPath,
                AudioDurationSeconds = duration.TotalSeconds,
                TargetAppName = flight.TargetApp,
                RawTranscript = null, CleanedTranscript = null,
            });
            Recovered?.Invoke("Nothing heard — recording kept in History.");
            return;
        }

        var noise = flight.Noise;
        await CleanAndInsertAsync(
            flight, wavPath, duration, raw,
            (float)(noise.FloorDB ?? 0), (float)noise.PeakDB, (float)(noise.MeasuredSNR ?? 0));
    }

    /// <summary>Shared tail: cleanup → gate → history row → insertion.</summary>
    private async Task CleanAndInsertAsync(
        FlightContext flight, string wavPath, TimeSpan duration,
        string raw, float floorDB, float peakDB, float snr)
    {
        var cleaned = raw;
        if (_settings.CleanupEnabled)
        {
            try
            {
                var tone = PromptV1.ToneForProcess(flight.TargetApp);
                using var cts = new CancellationTokenSource(TimeoutPolicy.CleanupDeadline(duration));
                cleaned = await _pipeline.ProcessAsync(raw, tone, ct: cts.Token);
                // The cleanup toggle is invisible mid-dictation: say which path
                // ran, so "the LLM didn't apply my change" is diagnosable from
                // the log alone instead of looking like a silent failure.
                _log(cleaned == raw
                    ? "cleanup: LLM output equals raw (no change or gate fallback)"
                    : "cleanup: LLM applied changes");
            }
            catch (Exception ex)
            {
                // Cleanup is optional polish; ASR text is already good (F9a).
                _log($"cleanup failed, using raw: {ex.Message}");
                FileLog.Write($"cleanup failed, using raw: {ex}");
                cleaned = raw;
            }
        }
        else
        {
            _log("cleanup: disabled in Settings → General — inserting raw ASR text");
        }

        _history.Upsert(new DictationRecord
        {
            Id = flight.SessionId, StartedAt = flight.StartedAt,
            Status = SessionStatus.Recorded, AudioPath = wavPath,
            RawTranscript = raw, CleanedTranscript = cleaned,
            AudioDurationSeconds = duration.TotalSeconds,
            ModelId = "nemotron-asr + local-llm",
        });

        // Esc'd between cleanup and insertion: the words were transcribed and
        // polished, but the user refused them — no insertion, no reveal. The
        // row KEEPS the transcript (that's what happened) which also lets
        // retention prune it like any other terminal row.
        if (_cancelRequested.TryRemove(flight.SessionId, out _))
        {
            _history.Upsert(new DictationRecord
            {
                Id = flight.SessionId, StartedAt = flight.StartedAt,
                Status = SessionStatus.Cancelled, AudioPath = wavPath,
                RawTranscript = raw, CleanedTranscript = cleaned,
                AudioDurationSeconds = duration.TotalSeconds,
                TargetAppName = flight.TargetApp,
            });
            SetStateIfCurrent(flight, DictationState.Cancelled);
            return;
        }

        SetStateIfCurrent(flight, DictationState.Inserting);
        var outcome = await _inserter.InsertAsync(cleaned, CancellationToken.None);
        var (status, evt) = outcome switch
        {
            InsertionOutcome.Inserted => (SessionStatus.Inserted, DictationEvent.Inserted),
            InsertionOutcome.FellBackToClipboard => (SessionStatus.CopiedToClipboard, DictationEvent.InsertionFellBackToClipboard),
            InsertionOutcome.BlockedSecure => (SessionStatus.HeldSecure, DictationEvent.InsertionBlockedSecure),
            _ => (SessionStatus.Failed, DictationEvent.TranscriptFailed),
        };
        _history.Upsert(new DictationRecord
        {
            Id = flight.SessionId, StartedAt = flight.StartedAt, Status = status,
            AudioPath = wavPath, RawTranscript = raw, CleanedTranscript = cleaned,
            AudioDurationSeconds = duration.TotalSeconds,
            PipelineSeconds = (DateTime.Now - flight.StartedAt).TotalSeconds,
            ModelId = "nemotron-asr + local-llm",
        });
        SetStateIfCurrent(flight, DictationStateMachine.Transition(DictationState.Inserting, evt) ?? DictationState.Done);

        // The reveal: only when insertion succeeded AND cleanup actually removed
        // something. Punctuation/casing-only changes show no reveal — the diff
        // marks them as kept by design (they are what cleanup ADDS).
        // Superseded-flight guard, same as SetStateIfCurrent: a slow session
        // finishing after a new dictation began must not paint its stale edit
        // over the new session's live partials.
        if (CorrectionReady is not null && flight.SessionId == _sessionId &&
            status == SessionStatus.Inserted &&
            _settings.CleanupEnabled && !ReferenceEquals(cleaned, raw))
        {
            var cuts = TranscriptDiff.Segments(raw, cleaned).Count(s => s.IsCut);
            if (cuts > 0)
                CorrectionReady?.Invoke(raw, cleaned);
        }
    }

    private async Task HandleTranscribeFailureAsync(
        FlightContext flight, string wavPath, TimeSpan duration, DictationFailure failure, string message)
    {
        FileLog.Write($"transcription failed ({failure}): {message}");
        var autoRetryable = RetryPolicy.DecisionFor(failure, 0) != RetryDecision.NotRetryable;
        var record = _history.Get(flight.SessionId);
        if (record is not null)
        {
            record.Status = autoRetryable ? SessionStatus.QueuedForRetry : SessionStatus.Failed;
            record.ErrorCode = failure.ToString();
            record.ErrorMessage = message;
            _history.Upsert(record);
        }

        if (autoRetryable)
        {
            // Queue for auto-retry (offline / server restarting). Manual Retry
            // from History remains available for every failure class either way.
            _retryQueue.Enqueue(new QueuedRetry
            {
                SessionId = flight.SessionId,
                AudioPath = wavPath,
                TargetApp = flight.TargetApp,
                ErrorCode = failure.ToString(),
                ErrorMessage = message,
                Attempt = 0,
                NextAttemptAt = DateTime.Now + RetryPolicy.BackoffFor(0),
            });
            Recovered?.Invoke($"{failure}: saved to History, retrying automatically.");
        }
        else
        {
            Recovered?.Invoke($"{failure}: saved to History — Retry from the History window.");
        }
        SetStateIfCurrent(flight, DictationState.Done);
    }

    /// <summary>The retry worker's engine: run audio through the CURRENT
    /// pipeline and update the row. Returns the cleaned text on success.</summary>
    public async Task<string?> RetryAsync(Guid sessionId)
    {
        var record = _history.Get(sessionId);
        if (record?.AudioPath is not { } wavPath || !File.Exists(wavPath))
        {
            Error?.Invoke("Retry: audio for that session is gone.");
            return null;
        }

        var duration = WavRecorder.DurationOf(wavPath);
        try
        {
            using var cts = new CancellationTokenSource(TimeoutPolicy.OverallDeadline(duration));
            var raw = await _transcriber.TranscribeAsync(wavPath, cts.Token);
            if (string.IsNullOrWhiteSpace(raw))
            {
                Error?.Invoke("Retry: still no speech in that recording.");
                return null;
            }
            var cleaned = raw;
            if (_settings.CleanupEnabled)
            {
                try
                {
                    using var cts2 = new CancellationTokenSource(TimeoutPolicy.CleanupDeadline(duration));
                    cleaned = await _pipeline.ProcessAsync(
                        raw, PromptV1.ToneForProcess(record.TargetAppName), ct: cts2.Token);
                }
                catch { cleaned = raw; }
            }
            else
            {
                _log("retry: cleanup disabled — inserting raw ASR text");
            }
            record.RawTranscript = raw;
            record.CleanedTranscript = cleaned;
            record.Status = SessionStatus.CopiedToClipboard;
            record.ErrorCode = null;
            record.ErrorMessage = null;
            _history.Upsert(record);
            return cleaned;
        }
        catch (Exception ex)
        {
            FileLog.Write($"retry failed for {sessionId}: {ex}");
            Error?.Invoke($"Retry failed: {ex.Message}");
            return null;
        }
    }

    private void Cancel(bool discardArtifacts = false)
    {
        // Esc during Finalizing/Transcribing/Inserting must still mean "stop":
        // the pipeline checks _cancelRequested before cleanup/insertion, and a
        // superseded flight learns of its cancellation here too.
        if (_state is DictationState.Finalizing or DictationState.Transcribing or DictationState.Inserting)
        {
            _cancelRequested[_sessionId] = 0;
            _log("cancel requested during processing — insertion will be skipped");
            SetState(DictationState.Cancelled);
            return;
        }
        if (_state is not (DictationState.Warming or DictationState.Recording)) return;
        SetState(DictationState.Cancelled);
        var recorder = _recorder;
        var stream = _stream;
        _recorder = null;
        _stream = null;
        if (stream is not null)
        {
            // Discard buffered audio server-side (input_audio_buffer.clear),
            // then close — best-effort, cancellation never blocks the UI.
            _ = Task.Run(async () =>
            {
                await stream.CancelAsync();
                await stream.DisposeAsync();
            });
        }
        if (recorder is not null)
        {
            var sid = _sessionId;
            var discard = discardArtifacts;
            _ = Task.Run(async () =>
            {
                string? finalPath = null;
                try { finalPath = await recorder.StopAsync(); }
                catch { /* cancelling: best-effort stop */ }
                if (discard)
                {
                    // Files are closed once StopAsync returns — only now can the
                    // folder actually go (deleting first would fail on the open
                    // writer and orphan everything, the race the old inline
                    // discard always lost).
                    try { Directory.Delete(SessionFolder(sid), true); } catch { }
                    return;
                }
                // The row was written with the in-progress capture path, which
                // StopAsync just transcoded away (capture.wav → audio.wav):
                // repoint it at the surviving file, or History/retention/retry
                // all reference a file that no longer exists.
                if (finalPath is not null)
                {
                    try
                    {
                        if (_history.Get(sid) is { } row)
                        {
                            row.AudioPath = finalPath;
                            _history.Upsert(row);
                        }
                    }
                    catch { /* best-effort */ }
                }
            });
        }
        _history.Upsert(new DictationRecord
        {
            Id = _sessionId, StartedAt = _startedAt, Status = SessionStatus.Cancelled,
            // Keep the audio reference: the upsert overwrites every column, and
            // null here orphaned the file on disk — untracked, unrecoverable,
            // invisible to retention. A discarded tap session references nothing.
            AudioPath = discardArtifacts ? null : recorder?.CurrentPath,
            TargetAppName = _targetApp,
        });
    }

    private void FailCurrent(DictationFailure kind, string message)
    {
        _history.Upsert(new DictationRecord
        {
            Id = _sessionId, StartedAt = _startedAt, Status = SessionStatus.Failed,
            ErrorCode = kind.ToString(), ErrorMessage = message, TargetAppName = _targetApp,
        });
        SetState(DictationState.Failed);
        Error?.Invoke($"{kind}: {message}");
    }

    private void FailFlight(FlightContext flight, DictationFailure kind, string message)
    {
        _cancelRequested.TryRemove(flight.SessionId, out _); // dead flight: drop any cancel intent
        _history.Upsert(new DictationRecord
        {
            Id = flight.SessionId, StartedAt = flight.StartedAt, Status = SessionStatus.Failed,
            ErrorCode = kind.ToString(), ErrorMessage = message, TargetAppName = flight.TargetApp,
        });
        SetStateIfCurrent(flight, DictationState.Failed);
        Error?.Invoke($"{kind}: {message}");
    }

    private static DictationFailure Classify(Exception ex) => ex switch
    {
        TranscriptionException { StatusCode: 401 } => DictationFailure.Auth,
        TranscriptionException { StatusCode: 403 or 404 } => DictationFailure.ModelAccess,
        TranscriptionException { StatusCode: 429 } => DictationFailure.RateLimited,
        TranscriptionException { StatusCode: 400 } => DictationFailure.BadRequest,
        OperationCanceledException => DictationFailure.Timeout,
        _ => DictationFailure.Network,
    };

    private void SetState(DictationState s)
    {
        if (_state == s) return;
        _state = s;
        try { StateChanged?.Invoke(s); }
        catch (Exception ex) { FileLog.Write($"StateChanged subscriber fault: {ex}"); }
    }

    /// <summary>An in-flight (superseded) task may only move the visible state
    /// if it is still the current session — overlap safety.</summary>
    private void SetStateIfCurrent(FlightContext flight, DictationState s)
    {
        if (flight.SessionId != _sessionId) return;
        SetState(s);
    }

    public static string SessionFolder(Guid id)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Athena", "sessions");
        return Path.Combine(root, id.ToString("N"));
    }

    /// <summary>Startup recovery: sessions from a previous run that never reached
    /// a terminal status become Recovered — their audio is on disk (the one
    /// invariant) and Retry brings the words back.</summary>
    public int RecoverCrashedSessions(DateTime appStart)
    {
        var recovered = 0;
        foreach (var r in _history.Recent(500))
        {
            if (r.Status is SessionStatus.Recording or SessionStatus.Transcribing
                && r.StartedAt < appStart.AddMinutes(-1))
            {
                r.Status = SessionStatus.Recovered;
                _history.Upsert(r);
                recovered++;
            }
        }
        return recovered;
    }

    /// <summary>Retention: terminal sessions older than the window lose audio;
    /// never delete before a transcript exists (the Superwhisper trap).</summary>
    public int PruneRetention(TimeSpan retention)
    {
        var pruned = 0;
        var cutoff = DateTime.Now - retention;
        foreach (var r in _history.Recent(2000))
        {
            if (r.StartedAt >= cutoff) continue;
            // QueuedForRetry/Recovered are NOT terminal: their audio is what a
            // retry needs — pruning it would break recovery. Cancelled rows are
            // the deliberate exception to the transcript rule: the user refused
            // those words, so the audio is prunable by age like any terminal row.
            var terminal = r.Status is SessionStatus.Inserted or SessionStatus.Cancelled
                or SessionStatus.Silent or SessionStatus.Failed or SessionStatus.CopiedToClipboard;
            var hasTranscript = r.CleanedTranscript is not null || r.RawTranscript is not null
                || r.Status == SessionStatus.Cancelled;
            if (!terminal || !hasTranscript) continue;
            if (r.AudioPath is { } path && File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    var dir = Path.GetDirectoryName(path);
                    if (dir is not null && Directory.GetFiles(dir).Length == 0) Directory.Delete(dir);
                    r.AudioPath = null;
                    _history.Upsert(r);
                    pruned++;
                }
                catch { /* retention is best-effort */ }
            }
        }
        return pruned;
    }

    public void Dispose()
    {
        _inFlight?.Wait(TimeSpan.FromSeconds(2));
        _recorder?.Dispose();
    }
}
