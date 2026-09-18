// Runtime accent-palette switcher. The base palette (Theme/Athena.xaml) routes
// every accent-consuming brush through DynamicResource into whichever variant
// dictionary (Theme/Accents/*.xaml) is currently merged into the application
// resources — swapping that one dictionary re-skins buttons, toggles, focus
// rings and selection colors in every open window, live, with no restart.

using System.Windows;

namespace Athena.App;

public static class ThemeManager
{
    public enum Accent
    {
        /// <summary>White highlight on near-black — the default palette.</summary>
        Neutral,
        /// <summary>Warm salmon highlight — the mockup palette.</summary>
        Warm,
    }

    private static readonly Dictionary<Accent, Uri> Sources = new()
    {
        [Accent.Neutral] = new("pack://application:,,,/Athena;component/Theme/Accents/Neutral.xaml"),
        [Accent.Warm] = new("pack://application:,,,/Athena;component/Theme/Accents/Warm.xaml"),
    };

    private static readonly object Gate = new();
    private static ResourceDictionary? _active;

    /// <summary>Merge the variant for <paramref name="accent"/> into the
    /// application resources, replacing any previously active one. Idempotent;
    /// safe before any window exists (the StartupUri windows load during
    /// base.OnStartup, so calling this there re-skins the first paint).</summary>
    public static void Apply(Accent accent)
    {
        lock (Gate)
        {
            var app = System.Windows.Application.Current;
            if (app is null) return;
            var merged = app.Resources.MergedDictionaries;
            if (_active is not null) merged.Remove(_active);
            var variant = new ResourceDictionary { Source = Sources[accent] };
            merged.Insert(0, variant);
            _active = variant;
            FileLog.Write($"theme: accent → {accent}");
        }
    }

    public static Accent FromSettings(bool warmAccent) => warmAccent ? Accent.Warm : Accent.Neutral;
}
