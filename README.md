<div align="center">

<img src="logo.png" width="96" alt="Athena">

# Athena for Windows

**Hold a key. Speak. It types — and it never leaves your machine.**

Local dictation for Windows: [Nemotron 3.5 ASR](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b)
for speech-to-text and a llama.cpp-served LLM for cleanup. No cloud, no API key,
no account. Your voice never touches the network.

<sub>A Windows port of <a href="mac_stuff/docs/MACOS_README.md">Jot for macOS</a> (Gemini edition) · Apache 2.0</sub>

</div>

---

## What it is

Hold `` ` ``, say the thing, let go. A moment later your words are in the app you
were already using — punctuated, filler words removed, self-corrections applied.
While you speak, your words appear **live** inside a small pill at the bottom of
the screen, streamed from the ASR model in real time.

```
hold ` ─▶ mic capture (WAV on disk from the first millisecond)
        └─▶ live PCM16 stream ─▶ Nemotron ASR ─▶ partial words in the pill, live
key up ─▶ full decode of the recording ─▶ LLM cleanup ─▶ validation gate
        ─▶ your words pasted at the cursor
```

It follows a change of mind: say *"let's meet at 1pm — actually, no, make it
2pm"* and Athena writes **"Let's meet at 2pm."** When cleanup removes words, the
pill shows you the edit — your raw wording, the cuts struck through in red, then
the sentence closing up as the fixed text — so the correction is never a silent
teleport.

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

## Install

A per-user installer (no admin rights) is built with Inno Setup:

```powershell
pwsh installer\build.ps1        # publish + compile -> dist\AthenaSetup-<version>.exe
```

Requires the [Inno Setup 6](https://jrsoftware.org/isinfo.php) compiler
(`winget install JRSoftware.InnoSetup`). The script publishes a self-contained
win-x64 build (no .NET runtime needed on the target machine) and compiles
`dist\AthenaSetup-<version>.exe`, which installs to
`%LocalAppData%\Programs\Athena`, adds a Start Menu group with an uninstaller,
and offers desktop / start-at-login shortcuts. User data (history DB, session
audio, logs, `settings.json`) survives uninstall and upgrades.

Run the tests (146 tests: pure-logic suites plus live integration tests that
auto-skip when the local servers are down):

```powershell
dotnet test Athena.sln
```

## The gestures

| Gesture | What happens |
| --- | --- |
| **Hold the dictation key** | Records while held — live text streams into the pill. Release and the text lands at your cursor. |
| **Key + tap `Space`** | Hands-free: keeps recording after you let go. Tap the key to finish. |
| **`Esc`** | Cancels; buffered audio is discarded server-side. |
| **Ctrl/Alt/Win + key** | Passes through to apps untouched — only the bare key belongs to Athena. |

The key is any lone key of your choice — rebind it in **Settings → General →
Dictation key** (persisted as `HotkeyVk` in `settings.json`, e.g. `0xC0` =
backtick).

## The pipeline

Each stage exists so the next one can be trusted — and every stage can fail
without losing words:

1. **Crash-safe capture** — WAV hits disk from t=0, fsynced. A crash, `taskkill /f`,
   or a dead battery costs nothing: on next launch, unfinished sessions are
   marked `Recovered` and their audio is intact (torn headers are repaired).
   The capture graph is also **prebuilt while idle** (a warm pool), so key-down
   pays only `StartRecording()` — the 100-odd ms of device activation is
   exactly where first words used to be lost.
2. **Live streaming ASR** — the mic's native-rate PCM streams over the server's
   realtime WebSocket (`/v1/audio/transcriptions/realtime`, deltas framed at
   100 ms, wire order = capture order via a single FIFO sender). Partial words
   render in the pill as you speak, with the server's own spacing preserved.
   The stream is **display-first**: it shows you your words while the recording
   is still being made.
3. **The whole-utterance decode** — at key-up, the on-disk WAV goes to the file
   endpoint (`POST /v1/audio/transcriptions`) and *that* transcript is what
   gets inserted. In local testing the file decode of a session was always
   complete while realtime streams occasionally dropped words, so the file is
   the record and the stream is the preview. (You can flip this: **Use the
   stream for the final text** in Settings trades the safety for lower latency,
   and **Double-check the first word** then corrects the stream's zero-left-
   context first word against the recording.) If the stream dies mid-word,
   nothing changes — the file decode was always the plan.
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

- **The three-beat HUD pill** — while speaking, the pill itself shows
  `ATHENA WRITES` with your words appearing live; if cleanup finds cuts, it
  flips to `YOU SAID` with your raw wording, the cuts strike through in red and
  collapse, settling on the fixed sentence that gets pasted. Never steals
  focus, click-through, scrollable for long dictations.
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
- **Honest server status** — the title-bar dot isn't cosmetic: every 5s both
  local servers' `/health` endpoints are probed, and while idle the dot is
  green only when they actually answer. A server going down turns the dot red
  with a message naming the server and the consequence ("ASR server down —
  speech won't transcribe"), before you waste a dictation finding out.
- **Fluent settings** — server URLs with live connection test, language picker,
  plain-language toggles for every behavior, rebindable dictation key, warm
  accent theme, launch-at-login.
- **Your logo everywhere** — the app icon (exe, tray, title bars) is generated
  from `logo.png` (`scripts/make_icon.py`).

## Configuration

Everything lives in `%APPDATA%\Athena\`:

| File | What |
|---|---|
| `settings.json` | servers, hotkey, language, toggles, retention |
| `dictionary.json` | terms + replacement rules |
| `history.db` | SQLite/FTS5 history of every session |
| `sessions\` | per-session audio (pruned by retention) |
| `logs\athena.log` | the diagnostic trail — every state transition lands here |

Handy `settings.json` keys (everything here is also a Settings → General
toggle):

```jsonc
{
  "AsrBaseUrl": "http://127.0.0.1:8080",
  "LlmBaseUrl": "http://127.0.0.1:3000",
  "LlmModel": null,              // blank = server default
  "Language": "en-US",           // or "auto", "de-DE", "hi-IN", ...
  "StreamingEnabled": true,      // live partial words in the pill
  "FileFallbackEnabled": false,  // true = insert the stream's final (lower latency)
  "CrossCheckAsr": true,         // fix the stream's first word vs the recording
  "CleanupEnabled": true,        // false = raw ASR output, no LLM
  "SoundsEnabled": true,         // earcons
  "WarmAccent": false,           // true = warm salmon accent theme
  "HotkeyVk": 192,               // 0xC0 = backtick
  "RetentionDays": 7,            // audio pruning (0 = keep forever)
  "HistoryLimit": 200
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
    TranscriptDiff.cs          the you-said → fixed alignment (pure)
    TranscriptSourcePolicy.cs  stream vs file decision matrix (pure)
    TrailingCapturePolicy.cs   keep listening past key-up (pure)
    PromptV1.cs                the cleanup steering prompt
    LanguageCatalog.cs         19 locales + auto-detect + tag stripping
    HistoryStore.cs            SQLite + FTS5 session history
    RetryQueue.cs              offline queue + backoff policy
    Clients/
      RealtimeAsrClient.cs     live WebSocket streaming (partials/finals)
      LocalAsrClient.cs        file endpoint (the record + retry path)
      LocalLlmClient.cs        OpenAI-compatible cleanup client (reasoning-aware)
  Athena.App/             the Windows shell
    DictationCoordinator.cs    orchestrates a dictation flight end-to-end
    Audio/WavRecorder.cs       WASAPI capture + live PCM16 tap + transcode
    Audio/WarmRecorderPool.cs  prewarmed capture graphs (key-down pays ~0)
    Interop/KeyboardHook.cs    WH_KEYBOARD_LL push-to-talk hook
    Interop/SendInputInserter.cs  the Ctrl+V insertion ladder
    Hud/HudPillWindow.cs       the non-activating pill: live text + edit reveal
    Sound/EarconPlayer.cs      synthesized earcons
    Windows/                   tray, main, Settings, History windows
tests/                    146 tests: pure-logic suites + live integration tests
scripts/                  dev probes + icon generator (python, uv-run)
docs/
  WINDOWS_PORT.md        port notes: what was mapped, what was trimmed
mac_stuff/               the original macOS app (Swift), untracked — reference only
```

## Troubleshooting

| Symptom | First look |
|---|---|
| Nothing pastes, text only in History | `%APPDATA%\Athena\logs\athena.log` — every transition is logged; if the last line is `Inserting` and the app died, it's the elevated-window guard (text is in the clipboard instead) |
| Key press does nothing | Is Athena in the tray? Is the target app elevated (UIPI blocks synthetic input into admin windows)? Does the key still type its own character — meaning another app owns a lower-level hook? |
| Live words never appear | Is **Show words while I'm speaking** on (Settings → General)? Is the ASR server up? The title-bar dot answers that at a glance (red = a server is down); the log shows `realtime connect failed` when the endpoint is missing — dictation still works, just without the live preview |
| Inserted text has wrong/cut words | The file decode owns the final by default; if you enabled **Use the stream for the final text**, try turning it off (the stream trades some accuracy for latency) |
| Transcription fails | Server up? `curl http://127.0.0.1:8080/health` and `curl http://127.0.0.1:3000/health` |
| App won't build | `dotnet --list-sdks` needs 10.x; kill any running `Athena.exe` first (file locks) |

## Acknowledgements

- **[NVIDIA NeMo-Speech.cpp](https://github.com/NVIDIA/NeMo-Speech.cpp)** and
  [Nemotron 3.5 ASR](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b) —
  local streaming speech recognition.
- **[llama.cpp](https://github.com/ggml-org/llama.cpp)** — local LLM inference.
- **[NAudio](https://github.com/naudio/NAudio)** — WASAPI capture and resampling.
- **[WPF UI (lepoco)](https://github.com/lepoco/wpfui)** — the Fluent window
  chrome, title bars, and toggle switches.
- **[Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)** —
  history storage with FTS5.
- **[xUnit](https://xunit.net/)** — the test net under all of it.

## License

Apache 2.0 — see [LICENSE](LICENSE).
