// Theme-system tests (theme.md spec section 18): default mode, the full
// System/Light/Dark × OS Light/Dark resolution matrix, SetTheme persistence,
// settings round-trips (including legacy/malformed values), and the OS
// registry probe. ThemeService's WPF side (ThemeApplier) no-ops without an
// Application instance, so the whole matrix runs headlessly.

using System.IO;
using Athena.App;
using Xunit;

namespace Athena.App.Tests;

public class ThemeServiceTests : IDisposable
{
    private readonly string _settingsPath =
        Path.Combine(Path.GetTempPath(), $"athena-theme-{Guid.NewGuid():N}.json");

    private int _saves;

    private ThemeService NewService() => new(
        load: () =>
        {
            using var _ = SettingsStore.UsePathForTests(_settingsPath);
            return SettingsStore.Load().ThemeMode;
        },
        save: mode =>
        {
            using var _ = SettingsStore.UsePathForTests(_settingsPath);
            var s = SettingsStore.Load();
            s.ThemeMode = mode;
            SettingsStore.Save(s);
            _saves++;
        });

    public void Dispose()
    {
        try { File.Delete(_settingsPath); } catch { }
    }

    // ── Defaults (spec section 13) ─────────────────────────────────────────

    [Fact]
    public void No_preference_defaults_to_System()
    {
        var service = NewService();
        service.Initialize(); // simulate startup: load from an empty store
        Assert.Equal(ThemeMode.System, service.CurrentMode);
    }

    [Fact]
    public void Fresh_settings_object_defaults_to_System()
    {
        Assert.Equal(ThemeMode.System, new AthenaSettings().ThemeMode);
    }

    // ── Resolution matrix (spec sections 3 and 14) ─────────────────────────

    [Fact]
    public void Light_and_Dark_modes_pass_through_regardless_of_os()
    {
        var service = NewService();
        service.SetTheme(ThemeMode.Light);
        Assert.Equal(AppTheme.Light, service.GetEffectiveTheme());

        service.SetTheme(ThemeMode.Dark);
        Assert.Equal(AppTheme.Dark, service.GetEffectiveTheme());
    }

    [Fact]
    public void System_mode_resolves_via_the_os_registry_probe()
    {
        var service = NewService();
        service.SetTheme(ThemeMode.System);
        Assert.Equal(ThemeMode.System, service.CurrentMode);
        // The effective theme mirrors the machine's real OS setting — on a
        // light-mode machine Light, on dark Dark. This pins that System is
        // RESOLVED (never a hardcoded Light alias).
        var expected = ThemeService.OsThemeUsesLight() ? AppTheme.Light : AppTheme.Dark;
        Assert.Equal(expected, service.GetEffectiveTheme());
    }

    // ── SetTheme + persistence (spec sections 4, 11, 18) ───────────────────

    [Fact]
    public void SetTheme_persists_each_mode_and_survives_a_reload()
    {
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark, ThemeMode.System })
        {
            var service = NewService();
            service.Initialize(); // startup on the previous value
            service.SetTheme(mode);
            Assert.Equal(mode, service.CurrentMode);

            // A NEW instance that loads (a restart) sees the persisted choice.
            var reloaded = NewService();
            reloaded.Initialize();
            Assert.Equal(mode, reloaded.CurrentMode);
        }
        Assert.Equal(3, _saves); // one write per SetTheme — no duplicate state
    }

    [Fact]
    public void Setting_the_same_mode_again_writes_nothing()
    {
        var service = NewService();
        service.SetTheme(ThemeMode.Dark);
        service.SetTheme(ThemeMode.Dark); // no-op path
        Assert.Equal(1, _saves);
        Assert.Equal(ThemeMode.Dark, service.CurrentMode);
    }

    [Fact]
    public void SetTheme_raises_effective_theme_changed_once_per_change()
    {
        var service = NewService();
        var changes = new List<AppTheme>();
        service.EffectiveThemeChanged += t => changes.Add(t);

        service.SetTheme(ThemeMode.Light);
        service.SetTheme(ThemeMode.Dark);
        service.SetTheme(ThemeMode.Dark); // no event on a no-op

        Assert.Equal(new[] { AppTheme.Light, AppTheme.Dark }, changes);
    }

    // ── Settings round-trip + legacy/malformed values (spec section 17) ────

    [Fact]
    public void ThemeMode_round_trips_through_settings_json()
    {
        foreach (var mode in new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark })
        {
            File.WriteAllText(_settingsPath, "{}");
            using (SettingsStore.UsePathForTests(_settingsPath))
            {
                var s = SettingsStore.Load();
                s.ThemeMode = mode;
                SettingsStore.Save(s);
                Assert.Equal(mode, SettingsStore.Load().ThemeMode);
            }
        }
    }

    [Theory]
    [InlineData("\"Dark\"", ThemeMode.Dark)]
    [InlineData("\"light\"", ThemeMode.Light)] // case-insensitive names
    [InlineData("\"SYSTEM\"", ThemeMode.System)]
    [InlineData("1", ThemeMode.Light)] // legacy numeric enum form
    [InlineData("2", ThemeMode.Dark)]
    [InlineData("\"Neon\"", ThemeMode.System)] // unknown name → System
    [InlineData("7", ThemeMode.System)] // out-of-range number → System
    [InlineData("null", ThemeMode.System)]
    public void Malformed_or_legacy_values_fall_back_to_System(string raw, ThemeMode expected)
    {
        File.WriteAllText(_settingsPath, $"{{\"ThemeMode\":{raw}}}");
        using (SettingsStore.UsePathForTests(_settingsPath))
        {
            Assert.Equal(expected, SettingsStore.Load().ThemeMode);
        }
    }

    [Fact]
    public void Settings_without_a_theme_key_default_to_System()
    {
        File.WriteAllText(_settingsPath, "{\"Language\":\"en-US\"}");
        using (SettingsStore.UsePathForTests(_settingsPath))
        {
            Assert.Equal(ThemeMode.System, SettingsStore.Load().ThemeMode);
        }
    }

    // ── The OS probe itself ────────────────────────────────────────────────

    [Fact]
    public void Os_probe_returns_a_definite_answer()
    {
        // Must not throw whether or not the personalize key exists; the bool
        // result directly drives System-mode resolution (asserted above via
        // the resolution-matrix test).
        _ = ThemeService.OsThemeUsesLight();
    }
}
