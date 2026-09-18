# Jot for Windows

A Windows port of [Jot for macOS](MACOS_README.md) — hold a hotkey, speak, and polished
text lands at your cursor — running **fully locally**: no cloud, no API key,
no accounts. Audio never leaves the machine.

```
hotkey down ─▶ WASAPI capture (WAV on disk from t=0) ─▶ key up ─▶ 16k mono WAV
                                                                    │
   cursor ◀─ Ctrl+V (SendInput) ◀─ [validation gate ◀─ local LLM] ◀─┘
                               (gate rejection falls back to raw ASR text)
                                                                    │
                                                     History (SQLite)
```

## Stack

| Layer | Choice |
|---|---|
| App | C# / .NET 10, WPF tray app (`src/Jot.App`) |
| Engine | `src/Jot.Core` — headless, zero UI deps, runs under plain `dotnet test` |
| ASR | Local Nemotron ASR server (`:8080`), OpenAI Whisper-compatible `/v1/audio/transcriptions` |
| LLM | Local llama.cpp `llama-server` (`:3000`), OpenAI-compatible `/v1/chat/completions` |
| Audio | NAudio WASAPI capture, native format → transcoded to 16 kHz mono after key-up |
| History | Microsoft.Data.Sqlite + FTS5 |

## Run

```bash
dotnet run --project src/Jot.App
```

Hold **`` ` ``** to dictate (rebindable to any VK in `%APPDATA%\Jot\settings.json`).
Release, and the cleaned text is pasted at your cursor. `Esc` cancels;
**`` ` `` + Space** locks hands-free. Chord shortcuts (Ctrl+`` ` `` etc.) pass
through to apps untouched.

Server endpoints are in `settings.json` too (`asrBaseUrl`, `llmBaseUrl`) —
both default to the localhost ports probed during the port.

## Layout

```
src/Jot.Core/            ported engine (pure logic, unit-testable headlessly)
  DictationStateMachine.cs   idle→warming→recording→…→done, pure transitions
  HotkeyProcessor.cs         hold/tap/Space-lock grammar, pure + clock-free
  ValidationGate.cs          "never insert garbage" gate (answer-mode, drift…)
  ReplacementEngine.cs       longest-match dictionary enforcement, case-preserving
  PromptV1.cs                cleanup prompt (rules, examples, tone map, dictionary)
  AudioLevelCurve.cs         the one 0…1 level definition + dB math
  HistoryStore.cs            SQLite + FTS5 history
  Clients/                   LocalAsrClient, LocalLlmClient (reasoning-aware)
src/Jot.App/             Windows shell
  DictationCoordinator.cs    orchestrates the whole pipeline
  Interop/KeyboardHook.cs    WH_KEYBOARD_LL hook on a dedicated thread (EventTap analog)
  Interop/SendInputInserter.cs   Ctrl+V ladder with secure-input refusal
  Audio/WavRecorder.cs       WASAPI capture + 16k transcode; Warm() pre-builds the graph
  Audio/WarmRecorderPool.cs  prewarmed spare so key-down pays only StartRecording()
  Audio/WavRepair.cs         crash recovery: repair torn WAV headers
  Windows/MainWindow.xaml    history list + status (HUD pill arrives later)
tests/Jot.Core.Tests/    91 tests (87 unit + 4 live smoke against your servers)
tests/Jot.App.Tests/     5 warm-pool tests (skip on machines without a mic)
```

## Tests

```bash
dotnet test                                  # unit only on machines without the servers
dotnet test --filter Category=Live           # live smoke: SAPI-synthesized speech → ASR → LLM → gate
```

Live tests synthesize real speech with Windows SAPI, send it through the ASR
server, clean it with the LLM, and assert the pipeline's guarantees (including
"never answers a question-shaped dictation"). They skip automatically when a
server is down.

## What's in (v1.x parity)

- **HUD pill** — bottom-center, topmost, non-activating (`WS_EX_NOACTIVATE`),
  click-through; live EMA waveform with per-bar phase, processing sweep,
  success/error terminal states, hands-free lock label.
- **Earcons** — the G-major family (start/stop/success/error/lock) synthesized
  at startup from EarconSynth; no sound files to ship; toggle in Settings.
- **Trailing capture** — key-up while still speaking keeps the mic open until
  0.25s quiet (1.5s cap), with the SNR-gated threshold from the macOS port.
- **Noise-floor estimator** — always runs, records floor/peak/SNR into rows;
  honest-silence rule (empty text + loud room is KEPT, never errored).
- **Dictionary** — terms suggest spellings in the prompt; wrong→right rules are
  enforced post-model; CSV import/export; newline smuggling neutralized.
- **Offline retry queue** — transient failures (server down, timeout) queue
  with 5s/30s/120s backoff, auto-drain every 10s, tray balloon when recovered;
  History Retry re-sends stored audio through the CURRENT pipeline.
- **Crash recovery** — non-terminal rows from a previous run become Recovered;
  torn WAV headers are repaired so audio is always playable.
- **Retention** — terminal sessions older than `RetentionDays` (default 7) lose
  audio; never before a transcript exists.
- **Overlapping sessions** — starting a new dictation while the old one is
  still transcribing/inserting works; flights carry their own context.
- **Settings window** — servers + connection test, cleanup/sounds toggles,
  retention, launch-at-login (Startup-folder shortcut), dictionary editor.
- **History window** — FTS search, raw/cleaned toggle, audio playback,
  per-row Copy/Retry/Delete (delete removes row + audio).
- **Push-to-talk** — lone backtick (or any VK via settings); Ctrl/Alt/Win+`
  passes through to apps; press-ID pairing makes key-repeat a non-event.
- **Live streaming transcription** — audio streams over the ASR server's
  realtime WebSocket (`/v1/audio/transcriptions/realtime`, the canonical
  NeMo-Speech.cpp path; `/v1/realtime` tried as legacy fallback) while you
  speak; partial words render in a HUD bubble above the pill and the streamed
  transcript is used directly at key-up (the file endpoint remains the
  always-on fallback for stream failures, plus the retry path). Toggle in
  Settings (`StreamingEnabled`). Cancel sends `input_audio_buffer.clear` so
  the server discards buffered audio.
- **Language selection** — the model's 19 transcription-ready locales
  (en-US default) **plus auto-detect** via Settings → General → Speech
  language; sent as the `language` hint on both the realtime session and the
  file endpoint. Auto mode's `<xx-XX>` output tags are stripped before the
  text reaches the gate or your editor.
- **ASR-level word boosting** — dictionary terms ride both the realtime
  `session.update` (`speech_contexts`) and the file endpoint as boost
  phrases, so jargon is spelled correctly by the ASR model itself, before
  the LLM or the replacement engine ever see the text.

## Known trims vs the macOS original

- Device-change mid-recording continues writing but logs no gap markers yet.
- No verbatim toggle / hold-Shift-verbatim yet.
- Insertion is one tier (synthesized Ctrl+V) + clipboard floor; no per-app
  quirks table yet.
