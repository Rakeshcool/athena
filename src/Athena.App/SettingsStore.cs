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

    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_WIN = 0x0008;

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

    [JsonIgnore]
    public string HotkeyDisplay => "`";
}

public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Athena");
    private static readonly string Path_ = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
        File.WriteAllText(Path_, JsonSerializer.Serialize(settings, Options));
    }
}
