// Windows-port original: the user's jargon, persisted as JSON at
// %APPDATA%\Athena\dictionary.json. Terms ride in the cleanup prompt (suggesting
// spellings to the model); the explicit wrong→right rules are then ENFORCED by
// ReplacementEngine after the model — the dictionary's guarantee. Mirrors
// DictionaryStore.swift + CSV import (quick-add is the Settings UI's job).

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athena.Core;

public sealed class DictionaryEntry
{
    public string Term { get; set; } = "";
}

public sealed class ReplacementEntry
{
    public string Wrong { get; set; } = "";
    public string Right { get; set; } = "";
}

public sealed class DictionaryData
{
    public List<DictionaryEntry> Terms { get; set; } = new();
    public List<ReplacementEntry> Replacements { get; set; } = new();
}

public sealed class DictionaryStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private DictionaryStore(string path) => _path = path;

    public static DictionaryStore Load() => LoadForTest(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Athena"));

    /// <summary>Directory-injectable load — tests use a temp dir, the app uses
    /// %APPDATA%\Athena.</summary>
    public static DictionaryStore LoadForTest(string directory)
    {
        var path = System.IO.Path.Combine(directory, "dictionary.json");
        var store = new DictionaryStore(path);
        store.TryLoadFromDisk(path);
        return store;
    }

    private readonly object _gate = new();
    private DictionaryData _data = new();

    public event Action? Changed;

    private void TryLoadFromDisk(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<DictionaryData>(File.ReadAllText(path), Options);
                if (data is not null) _data = data;
            }
        }
        catch { /* corrupt dictionary → defaults; entries are recoverable by editing the file */ }
    }

    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, Options));
        }
        Changed?.Invoke();
    }

    public List<string> TermsSnapshot()
    {
        lock (_gate)
            return _data.Terms.Select(t => t.Term).Where(t => t.Length > 0).ToList();
    }

    public List<ReplacementEngine.Rule> RulesSnapshot()
    {
        lock (_gate)
            return _data.Replacements
                .Where(r => r.Wrong.Length > 0 && r.Right.Length > 0)
                .Select(r => new ReplacementEngine.Rule(r.Wrong, r.Right))
                .ToList();
    }

    public DictionaryData Snapshot()
    {
        lock (_gate)
        {
            return new DictionaryData
            {
                Terms = _data.Terms.Select(t => new DictionaryEntry { Term = t.Term }).ToList(),
                Replacements = _data.Replacements
                    .Select(r => new ReplacementEntry { Wrong = r.Wrong, Right = r.Right }).ToList(),
            };
        }
    }

    public void ReplaceAll(DictionaryData data)
    {
        lock (_gate)
        {
            _data = new DictionaryData
            {
                // Newlines in dictionary entries would let a crafted CSV smuggle
                // prompt lines (audit L31) — neutralized at the door.
                Terms = data.Terms.Select(t => new DictionaryEntry
                { Term = t.Term.Replace("\n", " ").Replace("\r", " ").Trim() })
                    .Where(t => t.Term.Length > 0).ToList(),
                Replacements = data.Replacements.Select(r => new ReplacementEntry
                {
                    Wrong = r.Wrong.Replace("\n", " ").Replace("\r", " ").Trim(),
                    Right = r.Right.Replace("\n", " ").Replace("\r", " ").Trim(),
                })
                    .Where(r => r.Wrong.Length > 0 && r.Right.Length > 0).ToList(),
            };
        }
        Save();
    }

    public void AddTerm(string term)
    {
        // Same neutralization as import: a hand-typed entry is user data riding
        // in the prompt and must not be able to smuggle prompt lines.
        term = term.Replace("\n", " ").Replace("\r", " ").Trim();
        lock (_gate)
        {
            if (term.Length == 0 || _data.Terms.Any(t => t.Term.Equals(term, StringComparison.OrdinalIgnoreCase))) return;
            _data.Terms.Add(new DictionaryEntry { Term = term });
        }
        Save();
    }

    public void AddRule(string wrong, string right)
    {
        wrong = wrong.Replace("\n", " ").Replace("\r", " ").Trim();
        right = right.Replace("\n", " ").Replace("\r", " ").Trim();
        lock (_gate)
        {
            if (wrong.Length == 0 || right.Length == 0 ||
                _data.Replacements.Any(r => r.Wrong.Equals(wrong, StringComparison.OrdinalIgnoreCase)))
                return;
            _data.Replacements.Add(new ReplacementEntry { Wrong = wrong, Right = right });
        }
        Save();
    }

    /// <summary>CSV with header `wrong,right` or `term` rows (Athena's quick-add format).</summary>
    public void ImportCsv(string csv)
    {
        var data = new DictionaryData();
        foreach (var rawLine in csv.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(',');
            if (parts.Length >= 2)
                data.Replacements.Add(new ReplacementEntry { Wrong = parts[0], Right = parts[1] });
            else
                data.Terms.Add(new DictionaryEntry { Term = parts[0] });
        }
        // Import MERGES (ReplaceAll would let a bad CSV wipe the user's jargon).
        var current = Snapshot();
        data.Terms.AddRange(current.Terms);
        data.Replacements.AddRange(current.Replacements);
        ReplaceAll(data);
    }

    public string ExportCsv()
    {
        var snap = Snapshot();
        var lines = new List<string> { "# term or wrong,right" };
        lines.AddRange(snap.Terms.Select(t => t.Term));
        lines.AddRange(snap.Replacements.Select(r => $"{r.Wrong},{r.Right}"));
        return string.Join("\n", lines);
    }
}
