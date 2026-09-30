// Centralized theme state — spec sections 3, 5, 11, 12 (theme.md). One source
// of truth: ThemeMode (System/Light/Dark) in settings.json, resolved here to
// the effective Light/Dark palette and applied to every open window live.
//
// The mechanism mirrors the accent swap (ThemeManager + Theme/Accents/*.xaml):
// the surface/text palette lives in Theme/Palettes/Dark.xaml or Light.xaml and
// is merged into the application resources at position 0, so every
// DynamicResource consumer re-skins with no restart. WPF-UI's
// ApplicationThemeManager is re-applied alongside so its control theme
// (tooltips, combo popups, menus, focus visuals) follows too.
//
// System mode polls the Windows personalization registry key on a slow UI
// timer (theme flips are rare; polling needs no extra dependency and no
// window handle). WPF-UI's own SystemThemeWatcher is deliberately NOT used:
// it applies the OS theme unconditionally, which would override a forced
// Light/Dark choice — here the poll re-resolves and applies only when the
// EFFECTIVE theme actually differs, making forced modes immune to OS flips
// while System follows them live (spec sections 3, 14).

using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Athena.App;

public interface IThemeService
{
    /// <summary>The persisted preference (System/Light/Dark).</summary>
    ThemeMode CurrentMode { get; }

    /// <summary>The palette actually rendering right now: the mode resolved
    /// against the OS setting; Light/Dark pass straight through.</summary>
    AppTheme GetEffectiveTheme();

    /// <summary>Persist the mode, re-resolve, and re-skin every open window
    /// immediately — no restart (spec section 11).</summary>
    void SetTheme(ThemeMode mode);

    /// <summary>Raised after the EFFECTIVE theme changed (mode switch or an
    /// OS flip while in System mode).</summary>
    event Action<AppTheme>? EffectiveThemeChanged;
}

public sealed class ThemeService : IThemeService
{
    /// <summary>App-lifetime singleton (the one place global theme state
    /// lives — spec section 5 forbids scattering it). Tests build isolated
    /// instances through the constructor instead.</summary>
    public static ThemeService Instance { get; } = new();

    /// <summary>How often System mode re-checks the OS theme.</summary>
    public static readonly TimeSpan OsPollInterval = TimeSpan.FromSeconds(3);

    private readonly Func<ThemeMode> _load;
    private readonly Action<ThemeMode> _save;

    private ThemeMode _mode;
    private AppTheme _lastApplied = AppTheme.Dark;

    private ThemeService()
        : this(
            load: () => SettingsStore.Load().ThemeMode,
            save: mode =>
            {
                var s = SettingsStore.Load();
                s.ThemeMode = mode;
                SettingsStore.Save(s);
            })
    {
    }

    /// <summary>Test seam: injectable persistence. Apply hooks touch WPF only
    /// through ThemeApplier, which no-ops without an Application — tests can
    /// call Initialize/SetTheme and assert on mode/persistence/notification
    /// alone. NOTE: the mode is unset until Initialize() (production calls it
    /// from App.OnStartup; tests call it to simulate a restart).</summary>
    public ThemeService(Func<ThemeMode> load, Action<ThemeMode> save)
    {
        _load = load;
        _save = save;
    }

    /// <summary>Load the persisted preference (default System — spec section
    /// 13) and apply the resolved palette before any window paints. Called
    /// once from App.OnStartup; the OS poller is armed separately via
    /// StartOsThemePolling.</summary>
    public void Initialize()
    {
        _mode = _load();
        ApplyCurrent();
    }

    /// <summary>The persisted preference. Meaningful after Initialize() or
    /// SetTheme — the same lifetime as production (load at startup).</summary>
    public ThemeMode CurrentMode => _mode;

    public AppTheme GetEffectiveTheme() => _mode switch
    {
        ThemeMode.Light => AppTheme.Light,
        ThemeMode.Dark => AppTheme.Dark,
        _ => OsThemeUsesLight() ? AppTheme.Light : AppTheme.Dark,
    };

    public event Action<AppTheme>? EffectiveThemeChanged;

    public void SetTheme(ThemeMode mode)
    {
        if (mode == _mode) return;
        _mode = mode;
        _save(mode); // persist first: a crash can't lose the choice
        ApplyCurrent();
    }

    private void ApplyCurrent()
    {
        var effective = GetEffectiveTheme();
        ThemeApplier.Apply(effective);
        _lastApplied = effective;
        EffectiveThemeChanged?.Invoke(effective);
    }

    // ── OS integration ──────────────────────────────────────────────────────

    /// <summary>The Windows personalization setting: AppsUseLightTheme != 0
    /// means light apps. Unreadable registry keeps the dark-brand default.</summary>
    public static bool OsThemeUsesLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The System-mode follow loop: a slow UI-thread timer watching
    /// the OS key. Only System mode reacts; a forced Light/Dark ignores the
    /// flip (spec section 14's matrix), and nothing re-applies unless the
    /// effective theme actually changed. Armed from App.OnStartup.</summary>
    public static DispatcherTimer? StartOsThemePolling(ThemeService service)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return null;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = OsPollInterval,
        };
        timer.Tick += (_, _) =>
        {
            if (service.CurrentMode != ThemeMode.System) return;
            var effective = service.GetEffectiveTheme();
            if (effective == service._lastApplied) return;
            ThemeApplier.Apply(effective);
            service._lastApplied = effective;
            service.EffectiveThemeChanged?.Invoke(effective);
        };
        timer.Start();
        return timer;
    }
}

/// <summary>The WPF side of the swap, split from the logic so ThemeService's
/// mode/resolution/persistence behavior is unit-testable headlessly (spec
/// section 18). Every method no-ops safely when there is no Application.</summary>
public static class ThemeApplier
{
    private static System.Windows.ResourceDictionary? _activePalette;
    private static readonly object Gate = new();

    public static readonly IReadOnlyDictionary<AppTheme, Uri> PaletteSources =
        new Dictionary<AppTheme, Uri>
        {
            [AppTheme.Dark] = new("pack://application:,,,/Athena;component/Theme/Palettes/Dark.xaml"),
            [AppTheme.Light] = new("pack://application:,,,/Athena;component/Theme/Palettes/Light.xaml"),
        };

    /// <summary>Apply the effective theme to the whole app: WPF-UI control
    /// theme + the Athena surface palette + the window backdrop material,
    /// marshalled to the UI thread.</summary>
    public static void Apply(AppTheme theme)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() =>
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                theme == AppTheme.Light
                    ? Wpf.Ui.Appearance.ApplicationTheme.Light
                    : Wpf.Ui.Appearance.ApplicationTheme.Dark,
                Wpf.Ui.Controls.WindowBackdropType.Mica,
                false);
            SwapThemesDictionary(theme);
            MergePaletteOnUiThread(theme);
            UpdateWindowMaterials(theme);
        });
    }

    /// <summary>The missing half of live theme switching: a FluentWindow's
    /// visible surface is the DWM Mica material (the window's WPF Background
    /// is replaced with transparency at load), and its light/dark tint is the
    /// DWMWA_USE_IMMERSIVE_DARK_MODE flag — which WPF-UI sets ONLY when the
    /// window is created. Brush resources re-resolve on a theme change, but
    /// the material keeps the old tint, leaving dark text on a dark window
    /// until it is closed and reopened. Re-assert the flag for every open
    /// FluentWindow, then FORCE the compositor to rebuild the material: on a
    /// dark→light flip the flag alone is not enough — DWM keeps painting the
    /// old dark Mica until the backdrop attributes are re-applied (re-applying
    /// WPF-UI's backdrop) or the frame is invalidated (SWP_FRAMECHANGED
    /// nudge). Plain windows (the transparent HUD pill) are deliberately
    /// untouched, and ApplicationThemeManager.Apply's forceBackground:true was
    /// NOT used for the same reason — it rewrites every window's background,
    /// trampling the pill.</summary>
    private static void UpdateWindowMaterials(AppTheme theme)
    {
        foreach (var window in System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().ToArray())
        {
            if (window is Wpf.Ui.Controls.FluentWindow fluent)
                ApplyWindowMaterial(fluent, theme);
        }
    }

    /// <summary>Re-assert the DWM material on ONE window (flag + backdrop
    /// rebuild + frame nudge). Shared by the theme-change sweep and the
    /// window-loaded hook — the two moments a material can go stale.</summary>
    private static void ApplyWindowMaterial(System.Windows.Window fluent, AppTheme theme)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(fluent).Handle;
            if (hwnd == IntPtr.Zero) return; // not yet sourced
            var value = theme == AppTheme.Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
            try
            {
                // Re-issue the system-backdrop attributes so the material
                // re-creates with the NEW tint (the dark→light quirk).
                _ = Wpf.Ui.Controls.WindowBackdrop.ApplyBackdrop(
                    fluent, Wpf.Ui.Controls.WindowBackdropType.Mica);
            }
            catch
            {
                // Composition refused — the frame nudge below is the fallback.
            }
            NudgeFrame(hwnd);
        }
        catch
        {
            // Pre-20H1 Windows or a dying window: cosmetic only.
        }
    }

    /// <summary>WPF-UI tints each FluentWindow's material when the WINDOW is
    /// created — not from the applied app theme — so a window opened while a
    /// forced Light/Dark (or resolved System) mode is active could come up on
    /// the wrong material: at App.OnStartup time no window exists yet for the
    /// theme-change sweep to fix. This class handler re-asserts the material
    /// on every FluentWindow as it loads, for every window this process ever
    /// creates. Register once, before the StartupUri window loads.</summary>
    public static void RegisterWindowMaterialSync()
    {
        System.Windows.EventManager.RegisterClassHandler(
            typeof(System.Windows.Window),
            System.Windows.FrameworkElement.LoadedEvent,
            new System.Windows.RoutedEventHandler((sender, _) =>
            {
                if (sender is Wpf.Ui.Controls.FluentWindow fluent)
                    ApplyWindowMaterial(fluent, ThemeService.Instance.GetEffectiveTheme());
            }));
    }

    /// <summary>Force a WM_NCCALCSIZE pass so DWM recomputes the frame and
    /// re-renders the backdrop — cheap insurance when attribute re-application
    /// alone leaves a stale material. No move/size/activation happens.</summary>
    private static void NudgeFrame(IntPtr hwnd)
    {
        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const uint SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpNoZOrder = 0x0004,
        SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

    private static void MergePaletteOnUiThread(AppTheme theme)
    {
        lock (Gate)
        {
            var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
            if (_activePalette is not null) merged.Remove(_activePalette);
            var variant = new System.Windows.ResourceDictionary { Source = PaletteSources[theme] };
            // APPEND, not insert-at-0: WPF consults merged dictionaries from
            // last to first, so the palette only out-ranks WPF-UI's
            // ThemesDictionary when it comes AFTER it (same reason Athena.xaml
            // sits last in App.xaml). The accent swap inserts at 0 only
            // because its keys collide with nothing.
            merged.Add(variant);
            _activePalette = variant;
            FileLog.Write($"theme: palette → {theme}");
        }
    }

    /// <summary>Replace the ui:ThemesDictionary (WPF-UI's resource theme) so
    /// controls reading WPF-UI's OWN keys — tooltips, combo popups, menus —
    /// re-resolve to the matching light/dark set. Found by type, not position.</summary>
    private static void SwapThemesDictionary(AppTheme theme)
    {
        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        var target = theme == AppTheme.Light
            ? Wpf.Ui.Appearance.ApplicationTheme.Light
            : Wpf.Ui.Appearance.ApplicationTheme.Dark;
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i] is not Wpf.Ui.Markup.ThemesDictionary) continue;
            merged[i] = new Wpf.Ui.Markup.ThemesDictionary { Theme = target };
            break;
        }
    }
}
