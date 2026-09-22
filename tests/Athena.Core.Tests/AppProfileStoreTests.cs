// Tests for the target-app profile store: resolution rules, persistence
// round-trip, and the resolver's precedence over the built-in tone map.
// The store is directory-injected so tests run against a temp dir.

using Athena.Core;
using Xunit;

namespace Athena.Core.Tests;

public class AppProfileStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly AppProfileStore _store;

    public AppProfileStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "athena-profiles-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = AppProfileStore.LoadForTest(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Resolve_matches_case_insensitively_and_extension_free()
    {
        _store.SaveAll(new[] { new AppProfile { ProcessName = "WINWORD.EXE", Tone = ToneCategory.Email } });

        Assert.NotNull(_store.Resolve("winword"));
        Assert.NotNull(_store.Resolve("WinWord"));   // case-insensitive
        Assert.NotNull(_store.Resolve("winword.exe")); // extension tolerated
        Assert.Null(_store.Resolve("notepad"));
    }

    [Fact]
    public void Resolve_ignores_blank_names_and_null_input()
    {
        _store.SaveAll(new[] { new AppProfile { ProcessName = "  ", Tone = ToneCategory.Code } });

        Assert.Null(_store.Resolve("anything"));
        Assert.Null(_store.Resolve(null));
        Assert.Null(_store.Resolve(""));
    }

    [Fact]
    public void Resolve_returns_first_match_for_duplicate_names()
    {
        _store.SaveAll(new[]
        {
            new AppProfile { ProcessName = "code", SkipCleanup = true },
            new AppProfile { ProcessName = "code", Tone = ToneCategory.Code },
        });

        var hit = _store.Resolve("code");
        Assert.NotNull(hit);
        Assert.True(hit!.SkipCleanup);
    }

    [Fact]
    public void SaveAll_persists_across_instances()
    {
        _store.SaveAll(new[]
        {
            new AppProfile { ProcessName = "outlook", Tone = ToneCategory.Email, Language = "de-DE" },
            new AppProfile { ProcessName = "wt", SkipCleanup = true },
        });

        var reloaded = AppProfileStore.LoadForTest(_dir);
        var outlook = reloaded.Resolve("outlook");
        var terminal = reloaded.Resolve("wt");

        Assert.NotNull(outlook);
        Assert.Equal(ToneCategory.Email, outlook!.Tone);
        Assert.Equal("de-DE", outlook.Language);
        Assert.NotNull(terminal);
        Assert.True(terminal!.SkipCleanup);
    }

    [Fact]
    public void Empty_store_resolves_nothing()
    {
        Assert.Empty(_store.Snapshot());
        Assert.Null(_store.Resolve("code"));
    }

    [Fact]
    public void Corrupt_file_falls_back_to_empty()
    {
        File.WriteAllText(Path.Combine(_dir, "app-profiles.json"), "{ not json at all");
        var store = AppProfileStore.LoadForTest(_dir);

        Assert.Empty(store.Snapshot());
        Assert.Null(store.Resolve("code"));
    }

    [Fact]
    public void Remove_then_resolve_finds_nothing()
    {
        _store.SaveAll(new[] { new AppProfile { ProcessName = "slack", Tone = ToneCategory.WorkChat } });
        _store.SaveAll(Array.Empty<AppProfile>());

        Assert.Null(_store.Resolve("slack"));
        Assert.False(File.Exists(Path.Combine(_dir, "app-profiles.json.tmp")),
            "the tmp file must never survive a completed save");
    }

    [Fact]
    public void Tone_serializes_by_name_not_number()
    {
        _store.SaveAll(new[] { new AppProfile { ProcessName = "outlook", Tone = ToneCategory.Email } });
        var json = File.ReadAllText(Path.Combine(_dir, "app-profiles.json"));

        Assert.Contains("\"Tone\": \"Email\"", json);
        // Name-based files must round-trip (a number-form file would too, but
        // names keep the JSON human-editable — the point of the converter).
        var reloaded = AppProfileStore.LoadForTest(_dir);
        Assert.Equal(ToneCategory.Email, reloaded.Resolve("outlook")?.Tone);
    }

    [Fact]
    public void Legacy_number_form_still_deserializes()
    {
        // Tolerance for files written before the name-based serialization.
        File.WriteAllText(Path.Combine(_dir, "app-profiles.json"),
            """{"Profiles":[{"ProcessName":"code","Tone":3}]}""");

        var store = AppProfileStore.LoadForTest(_dir);
        Assert.Equal(ToneCategory.Code, store.Resolve("code")?.Tone);
    }
}
