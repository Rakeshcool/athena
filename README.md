<div align="center">

# Athena for Windows

**Hold a key. Speak. It types — and it never leaves your machine.**

Local dictation for Windows: [Nemotron 3.5 ASR](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b)
for speech-to-text and a llama.cpp-served LLM for cleanup. No cloud, no API key,
no account. Your voice never touches the network.

<sub>A Windows port of <a href="docs/MACOS_README.md">Jot for macOS</a> (Gemini edition) · Apache 2.0</sub>

</div>

---

## What it is

Hold `` ` ``, say the thing, let go. A moment later your words are in the app you
were already using — punctuated, filler words removed, self-corrections applied.
While you speak, your words appear **live** in a small pill at the bottom of the
screen, streamed from the ASR model in real time.

```
hold ` ─▶ mic capture (WAV on disk from the first millisecond)
        └─▶ live PCM16 stream ─▶ Nemotron ASR ─▶ partials in the HUD, live
key up ─▶ commit ─▶ final transcript ─▶ LLM cleanup ─▶ validation gate
        ─▶ your words pasted at the cursor
```

It follows a change of mind: say *"let's meet at 1pm — actually, no, make it
2pm"* and Athena writes **"Let's meet at 2pm."** That is the whole pitch, and you
can watch it happen live in the HUD while you're still talking.

## Requirements

| | |
|---|---|
| **OS** | Windows 10 (19041+) or Windows 11 |
| **.NET** | .NET SDK 10.0 (build) — the app itself targets `net10.0-windows` |
| **ASR server** | [NeMo-Speech.cpp](https://github.com/NVIDIA/NeMo-Speech.cpp) serving `nemotron-3.5-asr-streaming-0.6b` on `http://127.0.0.1:8080` |
| **LLM server** | [llama.cpp](https://github.com/ggml-org/llama.cpp) `llama-server` on `http://127.0.0.1:3000` (any OpenAI-compatible chat server works; reasoning models supported) |
| **Mic** | Any input device — capture runs in the device's native format |

Both servers are expected to already be running (the port was built against a
local setup; it never downloads models). Everything is localhost-only.

## Build & run

```powershell
# from the repo root
dotnet build Athena.sln -c Debug

# launch (tray app — a pill appears when you dictate)
src\Athena.App\bin\Debug\net10.0-windows10.0.19041.0\Athena.exe
```

Or in one step during development:

```powershell
dotnet run --project src/Athena.App
```

Run the tests (109 unit tests + 5 live integration tests; live tests
auto-skip when the local servers are down):

```powershell
dotnet test Athena.sln
```

## The gestures

| Gesture | What happens |
| --- | --- |
| **Hold `` ` ``** | Records while held — live text streams into the HUD. Release and the text lands at your cursor. |
| **`` ` `` + tap `Space`** | Hands-free: keeps recording after you let go. Tap `` ` `` to finish. |
| **`Esc`** | Cancels; buffered audio is discarded server-side. |
| **Ctrl/Alt/Win + `` ` ``** | Passes through to apps untouched — only the bare key belongs to Athena. |

The key is rebindable to any virtual key code in
`%APPDATA%\Athena\settings.json` (`HotkeyVk`, e.g. `0xC0` = backtick).

## The pipeline

Each stage exists so the next one can be trusted — and every stage can fail
without losing words:

1. **Crash-safe capture** — WAV hits disk from t=0, fsynced. A crash, `taskkill /f`,
   or a dead battery costs nothing: on next launch, unfinished sessions are
   marked `Recovered` and their audio is intact (torn headers are repaired).
   The capture graph is also **prebuilt while idle** (a warm pool, like the
   macOS original), so key-down pays only `StartRecording()` — the 100-odd ms
   of device activation is exactly where first words used to be lost.
2. **Live streaming ASR** — PCM16 mono 16 kHz streams over the server's
   realtime WebSocket (`/v1/audio/transcriptions/realtime`). Partial words
   render in the HUD as you speak. On key-up the server commits and the
   **streamed transcript is used directly** — no second transcription pass.
3. **The always-on fallback** — if the stream dies mid-word, the file endpoint
   (`POST /v1/audio/transcriptions`) transcribes the on-disk WAV instead. The
   stream is an optimization; the file is the record.
4. **LLM cleanup** — the raw transcript (already punctuated by the ASR model)
   goes to the local LLM with a steering prompt: filler removal, self-correction
   collapsing, per-app tone (Email / Work chat / Personal chat / Code / Neutral).
5. **Validation gate** — a <1ms semantic check of the LLM's output (did it answer
   the dictation instead of cleaning it? paraphrase-drift? hallucinated
   expansion? dropped content?). On rejection, the **raw** transcript is
   inserted — a high-quality fallback, never garbage.
6. **Replacement engine** — dictionary wrong→right rules applied as
   deterministic string operations. The LLM cannot override them.
7. **Insertion** — synthesized `Ctrl+V` (SendInput) at the cursor. Elevated
   windows are detected (UIPI) and refused: the text is copied to the clipboard
   with a tray balloon instead of blind-pasting into nowhere.

## Privacy

**Nothing leaves your machine. Ever.** The only network I/O in the entire app is
two localhost sockets: ASR on `127.0.0.1:8080`, LLM on `127.0.0.1:3000`. No
account, no API key, no telemetry, no analytics, no screenshots, no keystroke
logging. Every line of code that touches the network is in
[`src/Athena.Core/Clients/`](src/Athena.Core/Clients/) and you can read all of it.
Audio files live under `%APPDATA%\Athena\sessions\` and are pruned (transcripts
kept, audio dropped) after the retention window you set.

## Features

- **Live HUD pill** — waveform while recording, your words streaming in a
  bubble above it, green/red terminal states; never steals focus, click-through.
- **19 languages + auto-detect** — all of Nemotron 3.5's transcription-ready
  locales (English US/UK, Spanish ×2, French ×2, Italian, Portuguese ×2, Dutch,
  German, Turkish, Russian, Arabic, Hindi, Japanese, Korean, Vietnamese,
  Ukrainian), plus model-side **Auto-detect**. en-US default; language rides
  both the streaming session and the file path.
- **Dictionary with ASR-level word boosting** — terms are sent as
  `speech_contexts` to the ASR server, so jargon is spelled right *by the
  acoustic model*, before any cleanup runs. Wrong→right replacement rules
  enforce themselves afterwards. CSV import/export included.
- **Never lose words** — offline dictations queue (5s/30s/120s backoff) and
  auto-drain when the servers return; every failure is retryable from History;
  recovered sessions surface via tray balloon.
- **History** — full-text search (FTS5), raw/cleaned toggle, audio playback,
  per-row Copy/Retry/Delete. Retention prunes aged audio, never transcripts.
- **Earcons** — the Athena start/stop/success/error/lock sounds, synthesized at
  startup (no sound files shipped).
- **Settings** — server URLs with live connection test, language picker,
  cleanup/sounds toggles, retention, launch-at-login (Startup-folder shortcut).

## Configuration

Everything lives in `%APPDATA%\Athena\`:

| File | What |
|---|---|
| `settings.json` | servers, hotkey, language, toggles, retention |
| `dictionary.json` | terms + replacement rules |
| `history.db` | SQLite/FTS5 history of every session |
| `sessions\` | per-session audio (pruned by retention) |
| `logs\athena.log` | the diagnostic trail — every state transition lands here |

Handy `settings.json` keys:

```jsonc
{
  "AsrBaseUrl": "http://127.0.0.1:8080",
  "LlmBaseUrl": "http://127.0.0.1:3000",
  "Language": "en-US",          // or "auto", "de-DE", "hi-IN", ...
  "StreamingEnabled": true,      // false = batch mode (file endpoint only)
  "CleanupEnabled": true,        // false = raw ASR output, no LLM
  "HotkeyVk": 192                // 0xC0 = backtick
}
```

## Project layout

```
Athena.sln
src/
  Athena.Core/            the engine — headless, UI-free, fully unit-tested
    DictationStateMachine.cs   pure session-lifecycle transition function
    HotkeyProcessor.cs         hold/tap/lock/cancel grammar (pure)
    ValidationGate.cs          the never-insert-garbage gate (pure)
    ReplacementEngine.cs       deterministic wrong→right rules (pure)
    PromptV1.cs                the cleanup steering prompt
    LanguageCatalog.cs         19 locales + auto-detect + tag stripping
    HistoryStore.cs            SQLite + FTS5 session history
    RetryQueue.cs              offline queue + backoff policy
    Clients/
      RealtimeAsrClient.cs     live WebSocket streaming (partials/finals)
      LocalAsrClient.cs        file endpoint (fallback + retry path)
      LocalLlmClient.cs        OpenAI-compatible cleanup client (reasoning-aware)
  Athena.App/             the Windows shell
    DictationCoordinator.cs    orchestrates a dictation flight end-to-end
    Audio/WavRecorder.cs       WASAPI capture + live PCM16 tap + transcode
    Interop/KeyboardHook.cs    WH_KEYBOARD_LL push-to-talk hook
    Interop/SendInputInserter.cs  the Ctrl+V insertion ladder
    Hud/HudPillWindow.cs       the non-activating HUD pill + live bubble
    Sound/EarconPlayer.cs      synthesized earcons
    Windows/                   tray, main, Settings, History windows
tests/Athena.Core.Tests/  109 tests: pure-logic suites + 5 live integration tests
scripts/               dev probes (python, uv-run)
docs/
  WINDOWS_PORT.md      port notes: what was mapped, what was trimmed
mac_stuff/             the original macOS app (Swift), untracked — not part of the Windows build
```

## Troubleshooting

| Symptom | First look |
|---|---|
| Nothing pastes, text only in History | `%APPDATA%\Athena\logs\athena.log` — every transition is logged; if the last line is `Inserting` and the app died, it's the elevated-window guard (text is in the clipboard instead) |
| Key press does nothing | Is Athena in the tray? Is the target app elevated (UIPI blocks synthetic input into admin windows)? Does `` ` `` type a backtick — meaning another app owns a lower-level hook? |
| Streamed text never appears | Check `StreamingEnabled` in settings; the log shows `realtime connect failed` when the server lacks the endpoint (batch mode still works) |
| Transcription fails | Server up? `curl http://127.0.0.1:8080/health` and `curl http://127.0.0.1:3000/health` |
| App won't build | `dotnet --list-sdks` needs 10.x; kill any running `Athena.exe` first (file locks) |

## Acknowledgements

- **[Jot for macOS](mac_stuff/docs/MACOS_README.md)** by [Ammaar Reshi](https://x.com/ammaar) —
  the original app, design corpus, and failure-mode discipline this port follows.
- **[NVIDIA NeMo-Speech.cpp](https://github.com/NVIDIA/NeMo-Speech.cpp)** and
  [Nemotron 3.5 ASR](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b) —
  local streaming speech recognition.
- **[llama.cpp](https://github.com/ggml-org/llama.cpp)** — local LLM inference.
- **[NAudio](https://github.com/naudio/NAudio)** — WASAPI capture and resampling.
- **[Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)** —
  history storage with FTS5.
- **[xUnit](https://xunit.net/)** — the test net under all of it.

## License

Apache 2.0 — see [LICENSE](LICENSE).
