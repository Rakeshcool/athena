// Windows-port original: target-app profiles. The tone map inside PromptV1
// (Outlook → email, Slack → work chat, VS Code → code) answers "how should the
// model write" for the apps WE guessed — profiles answer it for the apps the
// USER uses, and can go further: skip cleanup entirely in a terminal, or force
// a transcription language for one app. Matching is on the process name the
// coordinator already captures (case-insensitive, extensionless — "winword",
// "code"); unknown apps fall back to the built-in PromptV1 map, so an empty
// profile list changes nothing.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athena.Core;

/// <summary>Per-application overrides keyed by process name.</summary>
public sealed class AppProfile
{
    /// <summary>Process name without extension, case-insensitive — "winword",
    /// "slack", "code". Empty entries are ignored at resolve time.</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>Tone for the cleanup prompt. Null = inherit the built-in
    /// process→tone map (PromptV1.ToneForProcess). Serialized by NAME, not
    /// number, so app-profiles.json stays human-readable and hand-editable.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ToneCategory? Tone { get; set; }

    /// <summary>Skip the LLM cleanup pass for this app — the raw ASR text
    /// inserts as-is. For terminals, IDEs, and anywhere the cleanup model
    /// mangles identifiers more than it fixes prose.</summary>
    public bool SkipCleanup { get; set; }

    /// <summary>BCP-47 transcription language override ("en-US", "hi-IN"…).
    /// Empty = follow the global language setting.</summary>
    public string Language { get; set; } = "";
}

public sealed class AppProfileData
{
    public List<AppProfile> Profiles { get; set; } = new();
}

public sealed class AppProfileStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private AppProfileStore(string path) => _path = path;

    public static AppProfileStore Load() => LoadForTest(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Athena"));

    /// <summary>Directory-injectable load — tests use a temp dir, the app uses
    /// %APPDATA%\Athena. Same pattern as DictionaryStore.</summary>
    public static AppProfileStore LoadForTest(string directory)
    {
        var path = Path.Combine(directory, "app-profiles.json");
        var store = new AppProfileStore(path);
        store.TryLoadFromDisk(path);
        return store;
    }

    private readonly object _gate = new();
    private AppProfileData _data = new();

    /// <summary>The resolved profile for a process name, or null. The ONLY
    /// place matching rules live: name equality is case-insensitive and
    /// extension-tolerant on BOTH sides — a user-typed "winword.exe" must
    /// match the coordinator's extensionless "winword", and vice versa.
    /// Blank profile names never match anything.</summary>
    public AppProfile? Resolve(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        var name = processName.Trim().ToLowerInvariant();
        var bare = name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
        lock (_gate)
        {
            foreach (var p in _data.Profiles)
            {
                if (string.IsNullOrWhiteSpace(p.ProcessName)) continue;
                var pn = p.ProcessName.Trim().ToLowerInvariant();
                // Compare BARE forms both sides: "WINWORD.EXE" (typed) matches
                // the coordinator's "winword", and "winword" (typed) matches
                // "winword.exe". A bare form covers every combination.
                var pnBare = pn.EndsWith(".exe", StringComparison.Ordinal) ? pn[..^4] : pn;
                if (pnBare == bare) return p;
            }
        }
        return null;
    }

    public List<AppProfile> Snapshot()
    {
        lock (_gate) return _data.Profiles.ToList();
    }

    /// <summary>Replace the whole set and persist (the Settings editor edits a
    /// list; per-row partial saves would fight the UI). Names are normalized —
    /// trimmed, case-collapsed at resolve time anyway, ".exe" tolerated.</summary>
    public void SaveAll(IEnumerable<AppProfile> profiles)
    {
        lock (_gate)
        {
            _data = new AppProfileData { Profiles = profiles.ToList() };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Atomic write (tmp + move), same rationale as settings.json: a
            // torn app-profiles.json previously loaded as defaults.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Options));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    private void TryLoadFromDisk(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                _data = JsonSerializer.Deserialize<AppProfileData>(File.ReadAllText(path), Options)
                        ?? new AppProfileData();
            }
        }
        catch { /* corrupt file falls back to empty — profiles are polish */ }
    }
}
