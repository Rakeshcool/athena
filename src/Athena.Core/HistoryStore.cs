// SQLite (Microsoft.Data.Sqlite) storage; the per-dictation folder with
// audio + meta stays the same shape so nothing is ever lost and every failure
// is retryable from History.

using Microsoft.Data.Sqlite;

namespace Athena.Core;

public enum SessionStatus
{
    Recording,
    Recorded,
    Transcribing,
    Inserted,
    CopiedToClipboard,
    AwaitingChip,
    HeldSecure,
    QueuedForRetry,
    Recovered,
    Silent,
    Cancelled,
    Failed,
}

public sealed class DictationRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime StartedAt { get; init; }
    public SessionStatus Status { get; set; } = SessionStatus.Recording;
    public string? TargetAppName { get; set; }
    public double? AudioDurationSeconds { get; set; }
    public string? AudioPath { get; set; }
    public string? RawTranscript { get; set; }
    public string? CleanedTranscript { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ModelId { get; set; }
    public double? PipelineSeconds { get; set; }

    /// <summary>Capture source: "mic", "system" (loopback-only take), or
    /// "mic+system" (both streams in one take, composed with a blank line).
    /// Null on rows from before the feature existed.</summary>
    public string? Source { get; set; }

    /// <summary>Per-word ASR timings as JSON (TranscriptTimeline), when the
    /// decode carried them. Kept OUT of the FTS index — search is text-only.
    /// Null on untimed rows (plain dictation path, legacy rows).</summary>
    public string? TimelineJson { get; set; }
}

public sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection _connection;

    // The coordinator's background pipeline AND the UI thread both write here;
    // Microsoft.Data.Sqlite connections are not thread-safe, and the resulting
    // "connection is already in a transaction" style faults were killing the
    // app mid-dictation. Every access serializes through this gate.
    private readonly object _gate = new();

    public HistoryStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        Migrate();
    }

    private void Migrate()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS dictations (
                id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                status TEXT NOT NULL,
                target_app_name TEXT,
                audio_duration_seconds REAL,
                audio_path TEXT,
                raw_transcript TEXT,
                cleaned_transcript TEXT,
                error_code TEXT,
                error_message TEXT,
                model_id TEXT,
                pipeline_seconds REAL
            );
            CREATE INDEX IF NOT EXISTS idx_started_at ON dictations(started_at DESC);
            CREATE VIRTUAL TABLE IF NOT EXISTS dictations_fts USING fts5(
                id UNINDEXED, raw_transcript, cleaned_transcript
            );
            """;
        cmd.ExecuteNonQuery();

        // Additive migrations: the capture-source column (loopback era) and
        // the per-word timeline JSON (timestamp era). SQLite ALTER TABLE ADD
        // COLUMN is null-filling, so pre-feature rows read null.
        foreach (var col in new[] { "source", "timeline" })
        {
            try
            {
                using var addCol = _connection.CreateCommand();
                addCol.CommandText = $"ALTER TABLE dictations ADD COLUMN {col} TEXT";
                addCol.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Column already exists — the only reason this fails.
            }
        }
    }

    public void Upsert(DictationRecord r)
    {
        lock (_gate)
        {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dictations (id, started_at, status, target_app_name, audio_duration_seconds,
                audio_path, raw_transcript, cleaned_transcript, error_code, error_message, model_id, pipeline_seconds, source, timeline)
            VALUES ($id, $started, $status, $app, $dur, $audio, $raw, $clean, $errc, $errm, $model, $pipe, $source, $timeline)
            ON CONFLICT(id) DO UPDATE SET
                status=$status, target_app_name=$app, audio_duration_seconds=$dur, audio_path=$audio,
                raw_transcript=$raw, cleaned_transcript=$clean, error_code=$errc, error_message=$errm,
                model_id=$model, pipeline_seconds=$pipe, source=COALESCE($source, source),
                timeline=COALESCE($timeline, timeline);
            INSERT INTO dictations_fts (id, raw_transcript, cleaned_transcript)
            SELECT $id, COALESCE($raw,''), COALESCE($clean,'')
            WHERE NOT EXISTS (SELECT 1 FROM dictations_fts WHERE id = $id);
            UPDATE dictations_fts SET raw_transcript=COALESCE($raw,''), cleaned_transcript=COALESCE($clean,'')
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", r.Id.ToString());
        cmd.Parameters.AddWithValue("$started", r.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$status", r.Status.ToString());
        cmd.Parameters.AddWithValue("$app", (object?)r.TargetAppName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dur", (object?)r.AudioDurationSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$audio", (object?)r.AudioPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$raw", (object?)r.RawTranscript ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$clean", (object?)r.CleanedTranscript ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$errc", (object?)r.ErrorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$errm", (object?)r.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)r.ModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pipe", (object?)r.PipelineSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", (object?)r.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$timeline", (object?)r.TimelineJson ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        }
    }

    public DictationRecord? Get(Guid id)
    {
        lock (_gate)
        {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM dictations WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? FromReader(reader) : null;
        }
    }

    public IReadOnlyList<DictationRecord> Recent(int limit = 100)
    {
        lock (_gate)
        {
        var list = new List<DictationRecord>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM dictations ORDER BY started_at DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(FromReader(reader));
        return list;
        }
    }

    public IReadOnlyList<DictationRecord> Search(string query, int limit = 100)
    {
        lock (_gate)
        {
        var list = new List<DictationRecord>();
        using var cmd = _connection.CreateCommand();
        // FTS5 query syntax errors on stray operators; quote the whole query so it
        // degrades to a phrase search instead of throwing.
        cmd.CommandText = """
            SELECT d.* FROM dictations d
            JOIN dictations_fts f ON d.id = f.id
            WHERE dictations_fts MATCH $q
            ORDER BY d.started_at DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$q", "\"" + query.Replace("\"", "\"\"") + "\"");
        cmd.Parameters.AddWithValue("$n", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(FromReader(reader));
        return list;
        }
    }

    public void DeleteAll()
    {
        lock (_gate)
        {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM dictations; DELETE FROM dictations_fts;";
        cmd.ExecuteNonQuery();
        }
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM dictations WHERE id=$id; DELETE FROM dictations_fts WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
        }
    }

    private static DictationRecord FromReader(SqliteDataReader r)
    {
        string? S(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));
        double? D(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetDouble(r.GetOrdinal(col));
        return new DictationRecord
        {
            Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
            StartedAt = DateTime.Parse(r.GetString(r.GetOrdinal("started_at"))),
            Status = Enum.TryParse<SessionStatus>(S("status"), out var st) ? st : SessionStatus.Failed,
            TargetAppName = S("target_app_name"),
            AudioDurationSeconds = D("audio_duration_seconds"),
            AudioPath = S("audio_path"),
            RawTranscript = S("raw_transcript"),
            CleanedTranscript = S("cleaned_transcript"),
            ErrorCode = S("error_code"),
            ErrorMessage = S("error_message"),
            ModelId = S("model_id"),
            PipelineSeconds = D("pipeline_seconds"),
            Source = r.IsDBNull(r.GetOrdinal("source")) ? null : S("source"),
            TimelineJson = r.IsDBNull(r.GetOrdinal("timeline")) ? null : S("timeline"),
        };
    }

    public void Dispose() => _connection.Dispose();
}
