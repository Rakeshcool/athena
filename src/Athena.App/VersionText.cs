// The app's version for the About page, from the build — never a hardcoded
// string that drifts from the csproj (and the installer's version source).

using System.Reflection;

namespace Athena.App;

public static class VersionText
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "?";
}
