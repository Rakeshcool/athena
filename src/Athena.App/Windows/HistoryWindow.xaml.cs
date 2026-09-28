// Windows-port original: the History window (HistoryWindow.swift). Search is
// FTS over raw+cleaned transcripts; rows toggle between cleaned and raw text;
// audio playback proves nothing was lost; Retry re-sends stored audio through
// the current pipeline (the "every failure is retryable" guarantee); Delete
// removes the row AND the audio (Delete All in the old window never swept a
// live session's folder — the coordinator still owns that guard).

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Athena.Core;

namespace Athena.App.Windows;

public sealed class HistoryRow
{
    public Guid Id { get; init; }
    public DateTime StartedAt { get; init; }
    public string Status { get; init; } = "";
    public string? TargetAppName { get; init; }
    public string? Cleaned { get; init; }
    public string? Raw { get; init; }
    public string? AudioPath { get; init; }
    public double? Duration { get; init; }
    public bool ShowRaw { get; set; }

    /// <summary>True when the row stores per-word ASR timings (SRT/VTT export
    /// and meeting interleaving work). The timeline column is only written
    /// with words in it, so presence of the JSON is the marker.</summary>
    public bool HasTimeline { get; init; }
    public string TimelineText => HasTimeline ? "timed" : "";

    public string DisplayText => (ShowRaw ? Raw ?? Cleaned : Cleaned ?? Raw) ?? "(no transcript)";
    public string DurationText => Duration is { } d ? $"{d:0.0}s" : "";
}

public partial class HistoryWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly HistoryStore _history;
    private readonly Func<Guid, Task> _retryRequested;

    public HistoryWindow(HistoryStore history, Func<Guid, Task> retryRequested)
    {
        InitializeComponent();
        _history = history;
        _retryRequested = retryRequested;
        Refresh();
        SearchBox.TextChanged += (_, _) => Refresh();
        Activated += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var showRaw = RawToggle.IsChecked == true;
        var rows = (string.IsNullOrWhiteSpace(SearchBox.Text)
                ? _history.Recent(200)
                : _history.Search(SearchBox.Text.Trim(), 200))
            .Select(r => new HistoryRow
            {
                Id = r.Id, StartedAt = r.StartedAt, Status = r.Status.ToString(),
                TargetAppName = r.TargetAppName, Cleaned = r.CleanedTranscript,
                Raw = r.RawTranscript, AudioPath = r.AudioPath,
                Duration = r.AudioDurationSeconds, ShowRaw = showRaw,
                HasTimeline = !string.IsNullOrEmpty(r.TimelineJson),
            })
            .ToList();
        HistoryList.ItemsSource = rows;
    }

    private HistoryRow? Selected => HistoryList.SelectedItem as HistoryRow;

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void OnToggleChanged(object sender, RoutedEventArgs e) => Refresh();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PlayButton.IsEnabled = Selected?.AudioPath is { } p && File.Exists(p);
    }

    private void OnPlayAudio(object sender, RoutedEventArgs e)
    {
        if (Selected?.AudioPath is not { } path || !File.Exists(path)) return;
        // Play via the default handler — MediaElement needs media codecs for the
        // raw capture format; mciSendString-style playback via SoundPlayer is
        // simpler: WAV in 16k mono PCM plays natively.
        try
        {
            var player = new System.Media.SoundPlayer(path);
            player.Play();
        }
        catch (Exception ex)
        {
            // Fluent UI: no system MessageBox chrome — surface errors as an
            // inline status line in the search box's placeholder position.
            SearchBox.PlaceholderText = $"Playback failed: {ex.Message}";
        }
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        await _retryRequested(row.Id);
        Refresh();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Selected?.DisplayText is { Length: > 0 } text)
            System.Windows.Clipboard.SetText(text);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        // Row + audio both go; the never-lose-words rule only protects sessions
        // that still exist — explicit user delete is the exception.
        if (row.AudioPath is { } audio && File.Exists(audio))
        {
            try
            {
                File.Delete(audio);
                var dir = Path.GetDirectoryName(audio);
                if (dir is not null && Directory.GetFiles(dir).Length == 0) Directory.Delete(dir);
            }
            catch { /* deletion must never block the UI */ }
        }
        _history.Delete(row.Id);
        Refresh();
    }

    /// <summary>Export the selected take in one of the ASR server's own
    /// response formats: SRT / WebVTT subtitles, plain JSON ({"text": …}) and
    /// JSON with timestamps (duration/language/task/text/words — verified
    /// against the server's dropdown). Everything is re-serialized from the
    /// per-word timings stored on the row; no re-transcription, no custom
    /// alignment. Rows from before the feature (or the stream-final path)
    /// carry no timings — the timed formats say so instead of writing an
    /// empty file; plain JSON works on any row.</summary>
    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var format = sender is System.Windows.Controls.Button { Tag: { } tag } ? tag.ToString() : "srt";
        var timed = TranscriptTimeline.FromJson(_history.Get(row.Id)?.TimelineJson);

        if (format != "json" && !timed.HasWords)
        {
            SearchBox.PlaceholderText = "No word timings on that take — Retry re-decodes it with timestamps.";
            return;
        }

        string content, ext, filter, suffix = "";
        switch (format)
        {
            case "json":
                // Server-faithful {"text": …}: the raw transcript (the ASR
                // model's own output), falling back to the cleaned text.
                content = TranscriptTimeline.ToPlainTextJson(timed.HasWords
                    ? timed
                    : new TimedTranscript(row.Raw ?? row.Cleaned ?? "", Array.Empty<TimedWord>()));
                ext = ".json";
                filter = "JSON transcript (*.json)|*.json";
                break;
            case "jsontimed":
                content = TranscriptTimeline.ToTimestampedJson(timed);
                ext = ".json";
                filter = "Timestamped JSON (*.json)|*.json";
                suffix = "-timed";
                break;
            case "vtt":
                content = TranscriptTimeline.ToVtt(timed.Words);
                ext = ".vtt";
                filter = "WebVTT subtitles (*.vtt)|*.vtt";
                break;
            default: // "srt"
                content = TranscriptTimeline.ToSrt(timed.Words);
                ext = ".srt";
                filter = "SubRip subtitles (*.srt)|*.srt";
                break;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"athena-{row.StartedAt:yyyy-MM-dd-HHmmss}{suffix}",
            DefaultExt = ext,
            Filter = filter,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, content);
            SearchBox.PlaceholderText = $"Export written: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            SearchBox.PlaceholderText = $"Export failed: {ex.Message}";
        }
    }
}
