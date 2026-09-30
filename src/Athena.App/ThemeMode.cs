// The app-wide theme mode — spec section 1 (theme.md): exactly three options,
// strongly typed, never scattered strings. System follows the OS light/dark
// preference (spec section 3), never an alias for Light.

namespace Athena.App;

public enum ThemeMode
{
    /// <summary>Follow the operating system's light/dark preference. The default.</summary>
    System,
    /// <summary>Always the light palette.</summary>
    Light,
    /// <summary>Always the dark palette.</summary>
    Dark,
}

/// <summary>The resolved palette a theme mode renders with.</summary>
public enum AppTheme
{
    Light,
    Dark,
}
