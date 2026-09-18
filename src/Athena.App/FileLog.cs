// Windows-port original: file logging to %APPDATA%\Athena\logs — the os_log analog.
// Every swallowable failure lands here; nothing dies silently.

using System.IO;

namespace Athena.App;

public static class FileLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string LogPath
    {
        get
        {
            if (_path is null)
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Athena", "logs");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "athena.log");
            }
            return _path;
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line);
                // Keep the log bounded — a dictation app has no business writing MBs.
                if (new FileInfo(LogPath).Length > 2_000_000)
                    File.WriteAllText(LogPath, line);
            }
            catch { /* logging must never throw */ }
        }
    }
}
