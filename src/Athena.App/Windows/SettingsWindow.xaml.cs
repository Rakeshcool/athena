// The Settings window. Owns the settings JSON, the dictionary editor + CSV
// round-trip, the server-connection test, and launch-at-login via the Startup
// shortcut (no extra dependency; registry Run key is the other common approach).

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Athena.App.Interop;
using Athena.Core;
using Athena.Core.Clients;

namespace Athena.App.Windows;

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly AthenaSettings _settings;
    private readonly DictionaryStore _dictionary;
    private readonly AppProfileStore _profiles;
    private readonly Action _settingsChanged;
    private readonly Interop.KeyboardHook? _hook;

    public SettingsWindow(AthenaSettings settings, DictionaryStore dictionary, Action settingsChanged,
        Interop.KeyboardHook? hook = null, AppProfileStore? profiles = null)
    {
        InitializeComponent();
        _settings = settings;
        _dictionary = dictionary;
        _profiles = profiles ?? AppProfileStore.Load();
        _settingsChanged = settingsChanged;
        _hook = hook;
        Load();
        // Push-to-talk key shows the live binding; "Change key" captures the
        // next keystroke through the hook (it must be the hook: the hook
        // consumes the current hotkey, WPF would never see it).
        HotkeyDisplay.Text = Interop.HotkeyName.For((ushort)settings.HotkeyVk);
        // Save on every change AND on close: this is a tray app — a user picks
        // Hindi and dictates while the window is still open. Persisting only on
        // Closed made the picker read like it did nothing (the coordinator kept
        // the old language until the window shut).
        // SelectionChanged (fires during Load) and Closed both funnel through
        // Save(); it is idempotent.
        // Implicit save, live: every control persists on change (the close-save
        // remains as a backstop). Each save flashes the "saved" tick.
        LanguageBox.SelectionChanged += (_, _) => Save();
        // Tone picker for the Profiles add-row: first item = inherit (null tag).
        NewProfileTone.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "(inherit app default)", Tag = null },
            new ComboBoxItem { Content = "Email", Tag = ToneCategory.Email },
            new ComboBoxItem { Content = "Work chat", Tag = ToneCategory.WorkChat },
            new ComboBoxItem { Content = "Personal chat", Tag = ToneCategory.PersonalChat },
            new ComboBoxItem { Content = "Code", Tag = ToneCategory.Code },
            new ComboBoxItem { Content = "Neutral", Tag = ToneCategory.Neutral },
        };
        NewProfileTone.SelectedIndex = 0;
        foreach (var toggle in new[] { CleanupToggle, SoundsToggle, StreamingToggle, FileFallbackToggle, CrossCheckToggle, SystemAudioToggle, LaunchAtLoginToggle, WarmAccentToggle })
        {
            toggle.Checked += (_, _) => Save();
            toggle.Unchecked += (_, _) => Save();
        }
        foreach (var radio in new[] { ThemeSystemRadio, ThemeLightRadio, ThemeDarkRadio })
        {
            radio.Checked += (_, _) => OnThemeRadioChanged();
        }
        PillTabs.SelectionChanged += (_, _) => ShowPage();
        Closed += (_, _) => Save();
    }

    /// <summary>Theme radios apply IMMEDIATELY (spec section 2: no restart):
    /// the choice lands on the shared settings object (so the next full save
    /// keeps it), persists through the ThemeService, and re-skins every open
    /// window. Suppressed while LoadSettings checks the boxes — that would
    /// otherwise re-apply the persisted mode on every window open.</summary>
    private bool _suppressThemeEvents;

    private ThemeMode? SelectedThemeMode =>
        ThemeSystemRadio.IsChecked == true ? Athena.App.ThemeMode.System :
        ThemeLightRadio.IsChecked == true ? Athena.App.ThemeMode.Light :
        ThemeDarkRadio.IsChecked == true ? Athena.App.ThemeMode.Dark : null;

    private void OnThemeRadioChanged()
    {
        if (_suppressThemeEvents || SelectedThemeMode is not { } mode) return;
        _settings.ThemeMode = mode;
        ThemeService.Instance.SetTheme(mode);
    }

    /// <summary>Pill-tab bar drives five content panels; the TabControl's own
    /// content is never used. The About page re-renders on every visit so its
    /// shortcut list reflects a rebind made earlier in the same session.</summary>
    private void ShowPage()
    {
        GeneralPage.Visibility = PillTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ServersPage.Visibility = PillTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        DictionaryPage.Visibility = PillTabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = PillTabs.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = PillTabs.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        if (PillTabs.SelectedIndex == 4) RefreshAbout();
    }

    /// <summary>Implicit save stays, but it's no longer silent: a brief
    /// "saved" tick in the title bar fades out (spec: confirmation on
    /// implicit save).</summary>
    private void ConfirmSaved()
    {
        SavedTick.Opacity = 1;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0,
            TimeSpan.FromMilliseconds(900))
        { BeginTime = TimeSpan.FromMilliseconds(700) };
        SavedTick.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Explicit Save (mockup's bottom bar). Everything already
    /// persists on change; this is the visible commitment + confirmation.</summary>
    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();

    /// <summary>Rebind gesture: arm the hook's capture mode, await one key
    /// (Esc cancels), persist and hot-swap the live hook. Guarded against
    /// rebinding mid-hold — the old key's up would never match the new
    /// binding and strand the press-pairing state.</summary>
    private async void OnRebindClick(object sender, RoutedEventArgs e)
    {
        if (_hook is null) return;
        if (_hook.HotkeyHeld || _hook.SessionActive)
        {
            HotkeyDisplay.Text = "finish the current dictation first";
            return;
        }

        RebindButton.Content = "Press a key…";
        RebindButton.IsEnabled = false;
        _hook.CaptureMode = true;
        var captured = new TaskCompletionSource<ushort?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCapture(ushort vk) => captured.TrySetResult(vk);
        void OnCancelled() => captured.TrySetResult(null);
        _hook.CaptureKeyDown += OnCapture;
        _hook.CaptureCancelled += OnCancelled;
        try
        {
            var vk = await captured.Task;
            _hook.CaptureMode = false;
            if (vk is { } key)
            {
                _settings.HotkeyVk = key;
                _hook.SetHotkey(key);
                Save(); // persist + "saved" tick + HUD hint refresh (below); the hook is already live
            }
        }
        finally
        {
            _hook.CaptureKeyDown -= OnCapture;
            _hook.CaptureCancelled -= OnCancelled;
            _hook.CaptureMode = false;
            RebindButton.Content = "Change key";
            RebindButton.IsEnabled = true;
            HotkeyDisplay.Text = Interop.HotkeyName.For((ushort)_settings.HotkeyVk);
        }
    }

    private void Load()
    {
        CleanupToggle.IsChecked = _settings.CleanupEnabled;
        SoundsToggle.IsChecked = _settings.SoundsEnabled;
        StreamingToggle.IsChecked = _settings.StreamingEnabled;
        FileFallbackToggle.IsChecked = _settings.FileFallbackEnabled;
        CrossCheckToggle.IsChecked = _settings.CrossCheckAsr;
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
        WarmAccentToggle.IsChecked = _settings.WarmAccent;
        SystemAudioToggle.IsChecked = _settings.SystemAudioEnabled;
        _suppressThemeEvents = true;
        (ThemeSystemRadio.IsChecked, ThemeLightRadio.IsChecked, ThemeDarkRadio.IsChecked) =
            _settings.ThemeMode switch
            {
                Athena.App.ThemeMode.Light => (false, true, false),
                Athena.App.ThemeMode.Dark => (false, false, true),
                _ => (true, false, false),
            };
        _suppressThemeEvents = false;
        RefreshDict();
        RefreshProfiles();
        RefreshAbout();
    }

    // --- About page (live shortcut list, version, privacy line) -----------

    /// <summary>Rebuild the About page's shortcut rows from LIVE settings:
    /// the dictation-key row follows a rebind, and the Space-gesture rows
    /// appear only while Capture system audio is on — exactly like the
    /// coordinator's own hints.</summary>
    private void RefreshAbout()
    {
        AboutVersion.Text = $"version {VersionText.Version}";
        var key = Interop.HotkeyName.For((ushort)_settings.HotkeyVk);
        ShortcutRows.Children.Clear();
        foreach (var (gesture, effect) in new[]
        {
            ($"Hold {key} and speak", "inserts polished text at the cursor when you release"),
            ($"Hold {key}, tap Space", "hands-free lock — keeps recording until you tap the key again"),
            ($"Tap {key}, then Space", $"captures system audio — the meeting, not your voice (tap {key} or Ctrl+Shift+S to finish)"),
            ("Esc", "cancels the take — nothing is pasted"),
            ("Ctrl+Shift+S", "stops a live take and pastes the result"),
        }.Where(r => SystemAudioShortcutApplies(r.Item1)))
        {
            AddShortcutRow(gesture, effect);
        }
        PrivacyText.Text = "Audio never leaves this machine: speech is transcribed by the local ASR server, then cleaned by the local LLM server — both on your PC, over localhost only.";
    }

    /// <summary>The hands-free lock and the system-audio latch exist only while
    /// Capture system audio is on (the grammar's opt-in flag).</summary>
    private bool SystemAudioShortcutApplies(string gesture) =>
        !(gesture.Contains("Space") && !_settings.SystemAudioEnabled);

    private void AddShortcutRow(string gesture, string effect)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var g = new TextBlock { Text = gesture, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var e = new TextBlock
        {
            Text = effect, FontSize = 12, Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(g, 0);
        Grid.SetColumn(e, 1);
        grid.Children.Add(g);
        grid.Children.Add(e);
        ShortcutRows.Children.Add(grid);
    }

    private void Save()
    {
        _settings.CleanupEnabled = CleanupToggle.IsChecked == true;
        _settings.SoundsEnabled = SoundsToggle.IsChecked == true;
        _settings.StreamingEnabled = StreamingToggle.IsChecked == true;
        _settings.FileFallbackEnabled = FileFallbackToggle.IsChecked == true;
        _settings.CrossCheckAsr = CrossCheckToggle.IsChecked == true;
        _settings.AsrBaseUrl = NormalizeUrl(AsrUrlBox.Text);
        _settings.LlmBaseUrl = NormalizeUrl(LlmUrlBox.Text);
        _settings.LlmModel = string.IsNullOrWhiteSpace(LlmModelBox.Text) ? null : LlmModelBox.Text.Trim();
        _settings.Language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string
            ?? Athena.Core.LanguageCatalog.Default;
        _settings.WarmAccent = WarmAccentToggle.IsChecked == true;
        _settings.SystemAudioEnabled = SystemAudioToggle.IsChecked == true;
        // The theme mode rides the shared settings object (a radio click
        // already persisted + applied it live); included here so a full save
        // never reverts it to an older value.
        if (SelectedThemeMode is { } mode) _settings.ThemeMode = mode;
        SettingsStore.Save(_settings);
        ThemeManager.Apply(ThemeManager.FromSettings(_settings.WarmAccent));
        _settingsChanged();
        ConfirmSaved();
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

    // --- Profiles tab -----------------------------------------------------

    private sealed record ProfileRow(string Name, string Tone, string Cleanup, string Language);

    private void RefreshProfiles()
    {
        ProfileList.ItemsSource = _profiles.Snapshot()
            .Select(p => new ProfileRow(
                p.ProcessName,
                p.Tone?.ToString() ?? "(auto)",
                p.SkipCleanup ? "skipped" : "on",
                string.IsNullOrWhiteSpace(p.Language) ? "(global)" : p.Language))
            .ToList();
    }

    private void OnAddProfile(object sender, RoutedEventArgs e)
    {
        var name = NewProfileBox.Text.Trim();
        if (name.Length == 0) return;
        // Unknown codes would ride both the stream config and the file request
        // raw — reject at the UI rather than let the app silently transcribe
        // that app with no language hint (the global catalog's doctrine:
        // IsSupported is advisory, the server owns the final say).
        var language = NewProfileLanguage.Text.Trim();
        if (language.Length > 0 && !Athena.Core.LanguageCatalog.IsSupported(language))
        {
            MessageBox.Show(this,
                $"'{language}' is not a language the ASR server supports. " +
                "Leave the field empty to follow the global setting, or pick a code like en-US, hi-IN.",
                "Athena");
            return;
        }
        // The add row edits ONE new profile: tone defaults to inherit (auto),
        // language empty = follow global. Editing existing rows means select →
        // remove → re-add, same model as the dictionary — good enough for a
        // five-field table and keeps implicit-save semantics simple.
        var profile = new AppProfile
        {
            ProcessName = name,
            Tone = (NewProfileTone.SelectedItem as ComboBoxItem)?.Tag as ToneCategory?,
            SkipCleanup = NewProfileSkip.IsChecked == true,
            Language = language,
        };
        var all = _profiles.Snapshot().Where(p =>
            !string.Equals(p.ProcessName.Trim(), name, StringComparison.OrdinalIgnoreCase));
        _profiles.SaveAll(all.Append(profile));
        NewProfileBox.Clear();
        NewProfileLanguage.Clear();
        NewProfileSkip.IsChecked = false;
        NewProfileTone.SelectedIndex = 0; // back to "(inherit app default)"
        RefreshProfiles();
        ConfirmSaved();
    }

    private void OnRemoveProfile(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileRow row) return;
        var remaining = _profiles.Snapshot().Where(p =>
            !string.Equals(p.ProcessName.Trim(), row.Name, StringComparison.OrdinalIgnoreCase));
        _profiles.SaveAll(remaining);
        RefreshProfiles();
        ConfirmSaved();
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
            // a .bat-free approach using the WScript.Shell COM object. Both paths
            // are single-quote-escaped: they embed the user profile path
            // (%USERNAME%), and a quote in either would corrupt the command —
            // PowerShell's '' inside '…' is the escape.
            static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";
            var psi = new ProcessStartInfo("powershell")
            {
                Arguments = $"-NoProfile -Command \"$s=(New-Object -ComObject WScript.Shell).CreateShortcut({PsQuote(ShortcutPath)});$s.TargetPath={PsQuote(exePath)};$s.Save()\"",
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
