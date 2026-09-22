# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Athena began as a macOS prototype ("Jot", cloud-backed, August 2026) and was
rebuilt from scratch as a fully local Windows application in September 2026.
The entries below cover the Windows application.

## [Unreleased]

### Added
- **Per-app profiles** (Settings → Profiles): overrides keyed on the foreground process name. Each profile can force a cleanup tone (or inherit the built-in app map), skip the LLM cleanup pass entirely — right for terminals and IDEs — and/or force a transcription language for that app, applied to both the realtime stream and the file decode. Unknown apps are unaffected; retries re-resolve the profile from the history row's app name. Persisted atomically to `app-profiles.json`.
- Global **Ctrl+Shift+S** shortcut that stops a live take and pastes the result. The chord is consumed only while a dictation session is active, so other apps' Ctrl+Shift+S bindings keep working; Esc continues to cancel and discard.
- A clickable **stop chip** on the HUD pill for hands-off takes (hold-key Space lock and tap+Space system-audio latch). It is visible before the first words arrive, and its label follows the current (rebindable) key name.
- Regression tests: the stop-shortcut chord matrix (exact match, Alt/Win exclusion, partial chords, key-repeat guard) and WPF HUD layout tests (lane visibility and geometry, steady pill height under animation, chip present before any text).

### Fixed
- The latch window timer never armed: a failed tap+Space gesture froze the hotkey grammar until Esc. The deadline is now armed from the grammar effects and cleared on every resolve and reset path.
- The cancel flag was consumed by the first pipeline half: an Esc'd dual (mic + system) take could half-insert. The flag is now shared read-only and removed once at the terminal gates; cancel-path history upserts merge instead of overwriting.
- A silent system-audio half no longer clobbers a dual-take session whose mic half produced words; only the composer's both-null path declares silence, with a source-specific message.
- `HasEmittedText` in the realtime ASR client is now `volatile` (written by the receive loop, read by the pipeline thread).
- Retry from History copies to the clipboard *before* writing the `CopiedToClipboard` row, so the status can no longer claim a copy that never happened.
- `settings.json` and `dictionary.json` saves are atomic (write to temp, then move), so a crash mid-write can no longer silently reset every toggle or wipe the dictionary.
- HUD waveform bars kept dancing on takes after the first once a processing state had run: the animation reset moved into `ShowPill` — the one entry point every take start passes through — restoring bar height and fill.
- Dual-take audio collisions: the loopback recorder writes `system-capture.wav` → `audio-system.wav`, so the mic and system writers can never target the same file and History playback keeps the correct recording.

## [0.1.0] - 2026-09-22

First Windows release — hold a key, speak, and polished text lands at your
cursor, running fully locally on Nemotron ASR and llama.cpp. Packaged as an
Inno Setup installer.

### Added

#### Core dictation
- Push-to-talk dictation on a rebindable key (default `` ` ``), captured through a low-level keyboard hook: hold to talk, release to transcribe; short taps become notes.
- Fully local pipeline: WASAPI microphone capture from a pre-warmed pool (a key press pays only engine start), local Nemotron ASR, a local llama.cpp cleanup pass guarded by a validation gate that falls back to the raw transcript, and insertion at the cursor via paste.
- Reliability semantics: audio is on disk from the first millisecond, every failure is retryable from History, and transient errors get one silent auto-retry.
- Dictionary of custom terms and wrong→right replacements (CSV import/export) that rides along with every request.
- Tone categories (email / chat / code / neutral), earcons, tray icon, and launch-at-startup.
- ASR language selection (en-US default) covering the model's supported language set.
- History window with timestamps, statuses, copy, and replay of saved session audio.

#### Live streaming
- Realtime streaming ASR over the Nemotron server's realtime WebSocket: words render in the HUD while you speak, degrading gracefully to file decode when streaming is off or unavailable.
- Streaming-vs-file cross-check ("first-word arbiter") toggle that corrects zero-left-context first-word errors using the file decode.
- Three-beat HUD pill: live partial words → a "you said → Jot wrote" reveal that strikes the fillers and shows the cleanup → the settled sentence. Word-gap spacing, scrollable text with no length cap, and a waveform that dances for as long as audio flows.

#### System audio (WASAPI loopback)
- Optional loopback capture of whatever plays through the default output device — Zoom/Meet calls, YouTube, Spotify, games — with no microphone involved.
- Dual-source takes: mic and system transcribe in parallel realtime sessions, shown side by side (`SYSTEM | MIC`) above a shared waveform. Either source can fail without taking the take down, and a loopback that cannot open degrades to mic-only.
- Hands-off gestures: hold key + Space to lock (mic and system keep recording), or tap key then Space within half a second to latch a system-only take. Finish with the key, the stop chip, or Ctrl+Shift+S; Esc cancels. One Settings toggle drives capture and the gestures.

#### App & packaging
- Fluent (WPF UI) desktop interface: custom title bar with rounded corners, dark theme with a warm-accent toggle, scrollable settings pages with an auto-hiding scrollbar, toggle switches, and server URL fields whose test-connection status reports real reachability.
- Windows installer (Inno Setup) with a build script, and an application icon.

### Changed
- Renamed the app from **Jot** to **Athena**; the macOS sources moved out of the Windows build and CI retargeted at Windows.
- Settings General tab labels rewritten in plain language.

### Fixed
- Live transcript reliability: glued words and deltas lost at key-up (FIFO sender matching the reference client's exact cadence), and trailing capture past key release.
- First-word homophone errors ("write a program" heard as "right program") via the arbiter, and Hindi recognition handling.
- The Settings window could not be reopened after closing until the app restarted.
- A silent transcode failure that produced empty transcripts on the fallback path.
- Security-audit fixes, including safe argument quoting for external processes.
