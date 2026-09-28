# Athena — Feature Plan & Implementation Instructions

Context: Athena for Windows is a local push-to-talk dictation app (.NET 10 / WPF).
ASR comes from Nemotron 3.5 served by NeMo-Speech.cpp (`127.0.0.1:8080`), and
cleanup comes from a llama.cpp LLM (`127.0.0.1:3000`). Everything stays on
localhost. This file collects the feature ideas and the implementation guidance
agreed so far.

---

## 1. Key principle: use what the Nemotron server already provides

**The Nemotron ASR server natively supports multiple response formats. Use them
instead of rebuilding the same things from scratch in Athena.**

The server's response-format dropdown offers:

| Format | Use in Athena |
| --- | --- |
| **JSON with timestamps** | Primary format for anything time-aware (meeting transcripts, history playback, pause-aware cleanup, stream-vs-file cross-check) |
| **JSON text** | Simple structured text result |
| **Plain text** | Current behavior; keep for normal dictation where speed matters |
| **SRT subtitles** | Direct export from History, with no custom subtitle generator needed |
| **WebVTT subtitles** | Same as SRT, for web/video players |

Timestamps are supported by the server by default but are **not yet implemented
in Athena**. Do not write custom alignment, subtitle-formatting, or timing logic
until the server's output has been checked and found insufficient. Where the
server already returns SRT/WebVTT, request that format and store or export it as
is.

> Verify against the real server before coding: the exact request parameter name
> for the response format, the JSON shape, and whether timestamps are
> per-word or per-segment. Per-word is needed for the pause-aware and
> click-to-seek features below.

---

## Current status (v0.1.0 released 2026-09-22)

Athena v0.1.0 ships with:
- Push-to-talk on a rebindable key, realtime streaming ASR, LLM cleanup + validation gate, History with audio playback
- System-audio loopback capture (mic + system dual lanes)
- Hands-off gestures (hold+Space lock, tap+Space latch), stop shortcut (Ctrl+Shift+S)
- Per-app profiles (tone override, cleanup skip, language forcing)
- Dictionary, tone categories, Settings UI, Inno Setup installer
- 160 tests (pure-logic, WPF regression, integration)

The Unreleased section in the changelog contains fixes and refinements to the
gestures and dual-take logic. Timestamps are still not captured or stored.

---

---

## 2. Timestamp implementation plan

1. **Richer ASR result type.** Extend `LocalAsrClient` to return text plus a list
   of segments or words with start and end times. Keep a plain-text path so
   normal dictation latency does not change.
2. **Request timestamps selectively.** Always for system-audio and long takes.
   For ordinary dictation, only if it adds no measurable latency (the pasted text
   does not need timing).
3. **Persist the timed result** in `history.db` (JSON column or a small segments
   table). Keep the FTS5 index on plain text only.
4. **Parser in `Athena.Core`** as a pure function (server JSON to segments; SRT/VTT
   to segments if needed), unit tested like the other pure-logic suites.
5. **Shared time origin.** For mic + system-audio takes, record each stream's
   start offset and add it to that stream's timestamps so both lanes sit on one
   timeline.

### What timestamps unlock

- **Chronological meeting transcripts.** Interleave mic and system audio in real
  order ("You: … / Them: …") instead of pasting your text, a blank line, then
  the system text.
- **SRT / WebVTT export** from the History window (server-provided formats).
- **Click-to-seek playback.** Highlight the current word during audio playback
  and jump to a spot by clicking a word.
- **Pause-aware cleanup.** Use gaps between words as paragraph-break hints and as
  self-correction cues ("actually…", "no, wait") in the LLM prompt.
- **Stream vs. file cross-check.** Align realtime-stream partials against the
  file decode by time to find exactly where the stream dropped words (more
  precise than the current first-word check).

---

## 3. Feature backlog

### Highest impact
- **Command mode (edit selected text by voice).** Select text, hold the key, and
  say "make this more formal" or "shorten this". The local LLM rewrites the
  selection and pastes it back.
- **Managed servers + first-run wizard.** Launch and stop `llama-server` and the
  ASR server, download models, unload when idle to free VRAM. Removes the
  biggest adoption barrier (both servers must currently be started manually).
- **Meeting mode.** Save system-audio sessions with timestamps, an LLM summary
  and action items, and export to Markdown/SRT/JSON. Speaker diarization is a
  stretch goal; "you vs. them" lane labels are the minimum.

### Dictation quality of life
- **Spoken editing commands:** "new line", "new paragraph", "scratch that",
  "delete last sentence", "all caps that".
- **Snippets / text expansion** ("my email" expands to the address), alongside
  the dictionary.
- **Paste-last and paste-raw hotkeys.** Re-insert the previous result, or insert
  the raw transcript when cleanup was too aggressive.
- **Editable cleanup prompt and tone profiles.** Per-app profiles already exist
  (Settings → Profiles: override tone, skip cleanup, force language per app). Extend them to let users edit the LLM prompt itself and create custom tone definitions, not just select from the built-in set.
- **Translation mode.** Dictate in one language and paste another (19 languages
  and a local LLM are already available).
- **Context-aware cleanup.** Read selected or surrounding text through UI
  Automation so casing and names continue naturally.

### History and learning
- **Learn from corrections.** Suggest dictionary terms when the same word is
  repeatedly fixed; add an "add to dictionary" button on history rows.
- **Re-run cleanup** on a history entry with a different tone or prompt.
- **Export and stats** (words per day, time saved).

### Robustness and input
- **Alternative triggers:** toggle mode, mouse side buttons, foot pedal or
  controller, plus VAD-based auto-stop for hands-free takes.
- **Noise suppression** (for example RNNoise) before ASR, and an in-app
  microphone picker.
- **UIA-based insertion fallback** for apps that block `Ctrl+V`; clipboard save
  and restore around the paste (if not already done).
- **Support-bundle export:** zip of scrubbed log plus settings.

### Longer shot
- **Local HTTP API / CLI** so scripts and other tools can use the same
  pipeline (transcribe a file through cleanup and the validation gate).

---

## 4. Suggested order

1. Timestamps via the server's existing response formats (this file, section 2).
   Start with either meeting-transcript interleaving or History export and
   playback.
2. Managed servers and first-run wizard (adoption).
3. Command mode (most new capability for the least new plumbing).
4. Snippets and custom tone profiles (small, daily-use).

The order depends on whether Athena is for personal use or will be distributed.
Distribution moves the managed-servers work to the front.

---

## 5. Guidance for whoever implements this

- Prefer the server's native capabilities (response formats, timestamps) over
  custom re-implementations.
- Keep the plain-text dictation path fast and unchanged; add timing on the side.
- Keep new logic pure and in `Athena.Core` where possible, with unit tests
  alongside the existing suites.
- Preserve the "never lose words" guarantees: crash-safe capture, retry queue,
  and the validation-gate fallback to raw text.
- Preserve privacy: nothing beyond the two localhost sockets.
