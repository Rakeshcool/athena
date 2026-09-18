// Windows-port original: the SettingsWindow (4-tab SwiftUI original → WPF
// tabs). Owns the settings JSON, the dictionary editor + CSV round-trip, the
// server-connection test, and launch-at-login via the Startup shortcut (no
// extra dependency; registry Run key is the other common approach).

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Athena.App.Interop;
using Athena.Core;
using Athena.Core.Clients;

namespace Athena.App.Windows;

public partial class SettingsWindow : Window
{
    private readonly AthenaSettings _settings;
    private readonly DictionaryStore _dictionary;
    private readonly Action _settingsChanged;

    public SettingsWindow(AthenaSettings settings, DictionaryStore dictionary, Action settingsChanged)
    {
        InitializeComponent();
        _settings = settings;
        _dictionary = dictionary;
        _settingsChanged = settingsChanged;
        Load();
        // Save on every change AND on close: this is a tray app — a user picks
        // Hindi and dictates while the window is still open. Persisting only on
        // Closed made the picker read like it did nothing (the coordinator kept
        // the old language until the window shut).
        // SelectionChanged (fires during Load) and Closed both funnel through
        // Save(); it is idempotent.
        LanguageBox.SelectionChanged += (_, _) => Save();
        StreamingToggle.Checked += (_, _) => Save();
        StreamingToggle.Unchecked += (_, _) => Save();
        Closed += (_, _) => Save();
    }

    private void Load()
    {
        CleanupToggle.IsChecked = _settings.CleanupEnabled;
        SoundsToggle.IsChecked = _settings.SoundsEnabled;
        StreamingToggle.IsChecked = _settings.StreamingEnabled;
        AsrUrlBox.Text = _settings.AsrBaseUrl;
        LlmUrlBox.Text = _settings.LlmBaseUrl;
        LlmModelBox.Text = _settings.LlmModel ?? "";
        var langs = Athena.Core.LanguageCatalog.All.ToList();
        LanguageBox.ItemsSource = langs
            .Select(l => new ComboBoxItem { Content = l.Name, Tag = l.Code })
            .ToList();
        var norm = Athena.Core.LanguageCatalog.Normalize(_settings.Language);
        LanguageBox.SelectedIndex = Math.Max(0, langs.FindIndex(l => l.Code == norm));
        LaunchAtLoginToggle.IsChecked = LaunchAtLogin.IsEnabled();
        RefreshDict();
    }

    private void Save()
    {
        _settings.CleanupEnabled = CleanupToggle.IsChecked == true;
        _settings.SoundsEnabled = SoundsToggle.IsChecked == true;
        _settings.StreamingEnabled = StreamingToggle.IsChecked == true;
        _settings.AsrBaseUrl = NormalizeUrl(AsrUrlBox.Text);
        _settings.LlmBaseUrl = NormalizeUrl(LlmUrlBox.Text);
        _settings.LlmModel = string.IsNullOrWhiteSpace(LlmModelBox.Text) ? null : LlmModelBox.Text.Trim();
        _settings.Language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string
            ?? Athena.Core.LanguageCatalog.Default;
        SettingsStore.Save(_settings);
        _settingsChanged();
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : $"http://{url}";
    }

    private void RefreshDict()
    {
        var snap = _dictionary.Snapshot();
        var rows = new List<DictRow>();
        rows.AddRange(snap.Terms.Select(t => new DictRow(t.Term, "(term)")));
        rows.AddRange(snap.Replacements.Select(r => new DictRow(r.Wrong, r.Right)));
        DictList.ItemsSource = rows;
    }

    private sealed record DictRow(string Left, string Right);

    private void OnAddTerm(object sender, RoutedEventArgs e)
    {
        var term = NewTermBox.Text.Trim();
        if (term.Length == 0) return;
        _dictionary.AddTerm(term);
        NewTermBox.Clear();
        RefreshDict();
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var wrong = NewTermBox.Text.Trim();
        var right = NewRightBox.Text.Trim();
        if (wrong.Length == 0 || right.Length == 0) return;
        _dictionary.AddRule(wrong, right);
        NewTermBox.Clear();
        NewRightBox.Clear();
        RefreshDict();
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e)
    {
        if (DictList.SelectedItem is not DictRow row) return;
        var snap = _dictionary.Snapshot();
        var data = new DictionaryData();
        bool removed = false;
        foreach (var t in snap.Terms)
        {
            if (!removed && t.Term == row.Left && row.Right == "(term)") { removed = true; continue; }
            data.Terms.Add(t);
        }
        foreach (var r in snap.Replacements)
        {
            if (!removed && r.Wrong == row.Left && r.Right == row.Right) { removed = true; continue; }
            data.Replacements.Add(r);
        }
        _dictionary.ReplaceAll(data);
        RefreshDict();
    }

    private void OnImportCsv(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "CSV files|*.csv|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _dictionary.ImportCsv(File.ReadAllText(dlg.FileName));
            RefreshDict();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Import failed: {ex.Message}", "Athena");
        }
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "athena-dictionary.csv", Filter = "CSV files|*.csv" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, _dictionary.ExportCsv());
    }

    private async void OnTestServers(object sender, RoutedEventArgs e)
    {
        Save();
        ServerStatus.Text = "Testing…";
        using var http = new System.Net.Http.HttpClient();
        var asr = new LocalAsrClient(http, _settings.AsrBaseUrl);
        var llm = new LocalLlmClient(http, _settings.LlmBaseUrl, _settings.LlmModel);
        var asrOk = await asr.IsHealthyAsync(CancellationToken.None);
        var llmOk = await llm.IsHealthyAsync(CancellationToken.None);
        ServerStatus.Text = $"ASR ({_settings.AsrBaseUrl}): {(asrOk ? "✔ reachable" : "✘ not reachable")}   " +
                            $"LLM ({_settings.LlmBaseUrl}): {(llmOk ? "✔ reachable" : "✘ not reachable")}";
    }
}

/// <summary>Launch-at-login via the user's Startup folder shortcut — visible in
/// Task Manager → Startup, removable without touching the registry.</summary>
public static class LaunchAtLogin
{
    private static string ShortcutPath
    {
        get
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            return Path.Combine(startup, "Athena.lnk");
        }
    }

    public static bool IsEnabled() => File.Exists(ShortcutPath);

    public static void SetEnabled(bool enabled, string exePath)
    {
        if (enabled == IsEnabled()) return;
        if (enabled)
        {
            // CreateShellLink via COM would need a type lib; the pragmatic path:
            // a .bat-free approach using the WScript.Shell COM object.
            var psi = new ProcessStartInfo("powershell")
            {
                Arguments = $"-NoProfile -Command \"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{ShortcutPath}');$s.TargetPath='{exePath}';$s.Save()\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
        }
        else if (File.Exists(ShortcutPath))
        {
            File.Delete(ShortcutPath);
        }
    }
}
