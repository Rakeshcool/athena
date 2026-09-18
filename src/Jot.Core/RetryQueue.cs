// Windows-port original: retry policy + queue persistence for the offline /
// server-down path. Philosophy carried from Jot's reliability spec: audio is
// already safe on disk before any network I/O; every failure is retryable from
// History; one silent auto-retry for transient classes; simple backoff — no
// 24h exponential ladders (critic reconciliation #5).

using System.Text.Json;

namespace Jot.Core;

public enum RetryDecision
{
    NotRetryable,
    RetryNow,
    RetryWithBackoff,
}

public static class RetryPolicy
{
    /// <summary>Failures that vanish on their own (server restarting, Wi-Fi
    /// blip) are worth one automatic retry; permanent failures are not.</summary>
    public static RetryDecision DecisionFor(DictationFailure failure, int attempt) => failure switch
    {
        DictationFailure.Network or DictationFailure.Timeout or DictationFailure.ServerUnreachable
            or DictationFailure.Audio
            => attempt < MaxAutoAttempts ? RetryDecision.RetryWithBackoff : RetryDecision.NotRetryable,
        DictationFailure.RateLimited => RetryDecision.RetryWithBackoff,
        // Auth/model/bad-request/safety will not change by trying again — but
        // History still offers a manual Retry for all of them (settings fixed,
        // model loaded, server swapped).
        _ => RetryDecision.NotRetryable,
    };

    public const int MaxAutoAttempts = 3;
    public static readonly TimeSpan[] BackoffSteps =
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(120),
    };

    public static TimeSpan BackoffFor(int attempt) =>
        BackoffSteps[Math.Min(attempt, BackoffSteps.Length - 1)];
}

/// <summary>A session waiting for its servers to come back.</summary>
public sealed class QueuedRetry
{
    public Guid SessionId { get; set; }
    public string AudioPath { get; set; } = "";
    public string? TargetApp { get; set; }
    public string ErrorCode { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public int Attempt { get; set; }
    public DateTime NextAttemptAt { get; set; }
}

public sealed class RetryQueueStore
{
    private static readonly string Path_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Jot", "retry-queue.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly object _gate = new();
    private List<QueuedRetry> _items = new();

    public static RetryQueueStore Load()
    {
        var store = new RetryQueueStore();
        try
        {
            if (File.Exists(Path_))
            {
                var items = JsonSerializer.Deserialize<List<QueuedRetry>>(File.ReadAllText(Path_), Options);
                if (items is not null) store._items = items;
            }
        }
        catch { /* corrupt queue → start empty; those sessions remain in History */ }
        return store;
    }

    private void Persist()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
        File.WriteAllText(Path_, JsonSerializer.Serialize(_items, Options));
    }

    public int Count { get { lock (_gate) return _items.Count; } }

    public void Enqueue(QueuedRetry item)
    {
        lock (_gate)
        {
            _items.RemoveAll(i => i.SessionId == item.SessionId);
            _items.Add(item);
            Persist();
        }
    }

    /// <summary>Items whose backoff has elapsed, oldest first. Audio files that
    /// vanished (retention, user cleanup) are dropped here.</summary>
    public List<QueuedRetry> Due(DateTime now)
    {
        lock (_gate)
        {
            var due = _items
                .Where(i => i.NextAttemptAt <= now)
                .Where(i => File.Exists(i.AudioPath))
                .OrderBy(i => i.NextAttemptAt)
                .ToList();
            foreach (var gone in _items.Where(i => !File.Exists(i.AudioPath)).ToList())
                _items.Remove(gone);
            Persist();
            return due;
        }
    }

    public void Remove(Guid sessionId)
    {
        lock (_gate)
        {
            _items.RemoveAll(i => i.SessionId == sessionId);
            Persist();
        }
    }

    /// <summary>Schedules the next attempt. Returns false when auto-retries are
    /// exhausted (the row stays in History for manual Retry).</summary>
    public bool Reschedule(QueuedRetry item, DictationFailure failure)
    {
        var decision = RetryPolicy.DecisionFor(failure, item.Attempt);
        if (decision == RetryDecision.NotRetryable)
        {
            Remove(item.SessionId);
            return false;
        }
        item.Attempt++;
        item.NextAttemptAt = DateTime.Now + RetryPolicy.BackoffFor(item.Attempt - 1);
        Enqueue(item);
        return true;
    }
}
