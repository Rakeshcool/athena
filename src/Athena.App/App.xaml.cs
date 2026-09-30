using System.IO;
using System.Windows;
using Athena.App.Audio;
using Athena.App.Interop;

namespace Athena.App;

public partial class App : System.Windows.Application
{
    /// <summary>The app-wide warm capture pool. Owned by MainWindow (the
    /// composition root) but disposed here: the tray's Exit goes through
    /// Application.Shutdown, not window teardown.</summary>
    public static WarmRecorderPool? WarmPool { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        MigrateLegacyDataDirectory();

        // Theme: the persisted ThemeMode (System default) resolves to the
        // effective palette BEFORE any window loads, so the first paint is
        // already on-theme (no light flash). System mode arms a registry
        // poller so OS light/dark flips are followed live; forced modes
        // ignore them. Accent palette (neutral vs warm) merges alongside —
        // the two swaps are orthogonal.
        ThemeService.Instance.Initialize();
        // Every FluentWindow re-asserts its backdrop material as it loads —
        // WPF-UI tints windows at creation, not from the applied app theme,
        // so a window opened under a forced Light/Dark mode could otherwise
        // come up on the wrong material (no open windows exist yet at
        // OnStartup for the theme-change sweep to have fixed).
        ThemeApplier.RegisterWindowMaterialSync();
        ThemeService.StartOsThemePolling(ThemeService.Instance);
        ThemeManager.Apply(ThemeManager.FromSettings(SettingsStore.Load().WarmAccent));

        // Tray app lifecycle: the main window is a VIEW, not the app's lifetime.
        // Closing it must leave Athena dictating from the tray — ShutdownMode
        // OnLastWindowClose (the WPF default) is what made the app die when the
        // window was closed, so that reopening showed history but no live app.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Nothing dies silently: background-task faults and UI-thread faults
        // land in %APPDATA%\Athena\logs\athena.log and keep the app alive.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            FileLog.Write($"UNOBSERVED TASK: {args.Exception}");
            args.SetObserved();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            FileLog.Write($"DISPATCHER: {args.Exception}");
            args.Handled = true; // degrade, don't die
        };

        base.OnStartup(e);
    }

    /// <summary>One-time migration: the app was called Jot before it was
    /// called Athena, and its data (settings.json, history.db, recordings,
    /// logs) lived in %APPDATA%\Jot. Move the whole directory once so users
    /// keep their history; failure is logged and non-fatal — a fresh
    /// directory is always an acceptable outcome. Must run before anything
    /// touches %APPDATA%\Athena.</summary>
    private static void MigrateLegacyDataDirectory()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var legacy = Path.Combine(appData, "Jot");
            var current = Path.Combine(appData, "Athena");
            if (!Directory.Exists(legacy) || Directory.Exists(current)) return;
            Directory.Move(legacy, current);
        }
        catch (Exception ex)
        {
            FileLog.Write($"data migration from %APPDATA%\\Jot failed: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Release the spare capture graph so the mic device object isn't held
        // until process teardown (clean exit for device indicators).
        WarmPool?.Dispose();
        WarmPool = null;
        base.OnExit(e);
    }
}
