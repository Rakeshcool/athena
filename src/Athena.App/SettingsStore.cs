// Windows-port original: settings persistence, mirroring SettingsStore.swift's
// role. JSON in %APPDATA%\Athena\settings.json (Keychain → DPAPI arrives with the
// API-key era; there are no secrets in the local-only port).

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athena.App;

public sealed class AthenaSettings
{
    public string AsrBaseUrl { get; set; } = "http://127.0.0.1:8080";
    public string LlmBaseUrl { get; set; } = "http://127.0.0.1:3000";
    public string? LlmModel { get; set; }

    /// <summary>BCP-47 language hint for the ASR (en-US default; see
    /// LanguageCatalog for the server-supported set).</summary>
    public string Language { get; set; } = Athena.Core.LanguageCatalog.Default;

    /// <summary>Stream audio over the realtime WebSocket so partial words
    /// appear live in the HUD while speaking. Display only — the final inserted
    /// text source is decided separately by <see cref="FileFallbackEnabled"/>.
    /// Off: no WebSocket session at all; the HUD stays empty while recording.</summary>
    public bool StreamingEnabled { get; set; } = true;

    /// <summary>Use the realtime stream's final transcript as the inserted text
    /// (skipping the file endpoint for latency) instead of always re-decoding
    /// the on-disk recording. Evidence for the default: a session's own audio
    /// replayed realtime dropped mid-stream words the file decode returned
    /// completely — the whole-utterance decode is the safer final source.</summary>
    public bool FileFallbackEnabled { get; set; } = false;

    /// <summary>After a streamed dictation, also transcribe the on-disk recording
    /// and prefer it when the two disagree on the FIRST word. The stream decides
    /// each word with zero left context at utterance start — the classic
    /// "Write" heard as "Right" — while the file decode sees the whole utterance
    /// at once. Costs one local file transcription per dictation; the stream text
    /// always stands if the recording can't be transcribed.</summary>
    public bool CrossCheckAsr { get; set; } = true;

    /// <summary>One-time migration of the pre-split StreamingEnabled key
    /// (it controlled both partials and final source): a user who had streaming
    /// off wanted no stream text anywhere — keep both off; a user on the old
    /// default (on) gets today's default shape. Persisted so the migration
    /// runs exactly once and never clobbers a later user choice.</summary>
    public bool MigratedSplitToggles { get; set; }

    [JsonIgnore]
    public bool NeedsSplitTogglesMigration => !MigratedSplitToggles;

    /// <summary>Push-to-talk hotkey: a LONE key (default backtick, VK_OEM_3).
    /// Bare ` (and Shift+`) is Athena's; ` with Ctrl/Alt/Win passes through so app
    /// shortcuts keep working. The modifiers field is retained for future chord
    /// support but the hook ignores it.</summary>
    public uint HotkeyModifiers { get; set; } = 0;
    public uint HotkeyVk { get; set; } = 0xC0; // VK_OEM_3 — backtick

    public bool CleanupEnabled { get; set; } = true;
    public bool SoundsEnabled { get; set; } = true;
    public int HistoryLimit { get; set; } = 200;

    /// <summary>Days terminal sessions keep their audio (0 = keep forever).
    /// Nothing is pruned before a transcript exists — the never-lose-words rule.</summary>
    public int RetentionDays { get; set; } = 7;

    public bool LaunchAtLogin { get; set; }

    /// <summary>Appearance → warm salmon accent (the mockup palette) instead of
    /// the default neutral white highlight. Applies live; persisted like every
    /// other setting.</summary>
    public bool WarmAccent { get; set; }

    /// <summary>App theme: System follows the OS light/dark preference (the
    /// default — never an alias for Light), Light/Dark force a palette.
    /// Serialized as its name ("System"/"Light"/"Dark") for a human-editable
    /// settings.json; unreadable values fall back to System on load.</summary>
    [JsonConverter(typeof(ThemeModeJsonConverter))]
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;

    /// <summary>System-audio dictation (WASAPI loopback): capture whatever the
    /// default OUTPUT device is playing — a Zoom/Meet call, YouTube, Spotify —
    /// alongside the microphone on every take. Tap + Space (or the hotkey a
    /// second time) instead starts a loopback-ONLY take with the mic muted.
    /// Mic behavior is completely unchanged when this is off (the default).</summary>
    public bool SystemAudioEnabled { get; set; }

    /// <summary>BCP-47 hint for the SYSTEM-AUDIO transcript (speaker language —
    /// a Hindi meeting with an English UI is the common case). Empty = follow
    /// the mic language. Independent of <see cref="Language"/> because the two
    /// streams rarely speak the same language.</summary>
    public string AudioLanguage { get; set; } = "";
}

/// <summary>Reads ThemeMode leniently: a name ("Dark"), a number, or an
/// unknown value all fall back to System instead of throwing — one bad key in
/// settings.json must never reset every other setting (same rule as the
/// legacy-number tolerance in app-profiles.json).</summary>
public sealed class ThemeModeJsonConverter : JsonConverter<ThemeMode>
{
    public override ThemeMode Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return Enum.TryParse<ThemeMode>(reader.GetString(), ignoreCase: true, out var m)
                ? m : ThemeMode.System;
        }
        if (reader.TokenType == JsonTokenType.Number)
        {
            var n = reader.GetInt32();
            return n is >= 0 and <= 2 ? (ThemeMode)n : ThemeMode.System;
        }
        return ThemeMode.System;
    }

    public override void Write(Utf8JsonWriter writer, ThemeMode value, JsonSerializerOptions o)
        => writer.WriteStringValue(value.ToString());
}

public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Athena");
    private static string? _pathOverride; // tests: redirect settings.json to a temp file
    private static string Path_ => _pathOverride ?? Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Test-only: point the store at another file for the duration
    /// of the using-block. Static state, so tests using it must not run in
    /// parallel with other store tests — xUnit serializes within a class.</summary>
    public static IDisposable UsePathForTests(string path)
    {
        _pathOverride = path;
        return new PathOverride();
    }

    private sealed class PathOverride : IDisposable
    {
        public void Dispose() => _pathOverride = null;
    }

    public static AthenaSettings Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var s = JsonSerializer.Deserialize<AthenaSettings>(File.ReadAllText(Path_), Options)
                        ?? new AthenaSettings();
                Migrate(s);
                return s;
            }
        }
        catch { /* corrupt settings fall back to defaults */ }
        return new AthenaSettings();
    }

    /// <summary>Forward-compatibility migrations, applied on every load.</summary>
    private static void Migrate(AthenaSettings s)
    {
        if (s.NeedsSplitTogglesMigration)
        {
            s.FileFallbackEnabled = false; // whole-utterance decode as the final source
            s.MigratedSplitToggles = true;
        }
    }

    public static void Save(AthenaSettings settings)
    {
        Directory.CreateDirectory(Dir);
        // Atomic write: a crash or power-cut mid-WriteAllText previously left a
        // torn settings.json that loaded as defaults — every toggle silently
        // reset. tmp + move is all-or-nothing.
        var tmp = Path_ + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, Path_, overwrite: true);
    }
}
