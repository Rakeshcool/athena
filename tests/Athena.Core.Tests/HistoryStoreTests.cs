using Athena.Core;
using Xunit;

namespace Athena.Core.Tests;

public class HistoryStoreTests : IDisposable
{
    private readonly HistoryStore _store;
    private readonly string _path;

    public HistoryStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"athena-test-{Guid.NewGuid():N}.db");
        _store = new HistoryStore(_path);
    }

    [Fact]
    public void Upsert_and_get_roundtrip()
    {
        var id = Guid.NewGuid();
        _store.Upsert(new DictationRecord
        {
            Id = id, StartedAt = DateTime.Now, Status = SessionStatus.Recording,
        });
        _store.Upsert(new DictationRecord
        {
            Id = id, StartedAt = DateTime.Now, Status = SessionStatus.Inserted,
            RawTranscript = "um hello world", CleanedTranscript = "Hello, world!",
            AudioDurationSeconds = 1.5,
        });

        var got = _store.Get(id);
        Assert.NotNull(got);
        Assert.Equal(SessionStatus.Inserted, got!.Status);
        Assert.Equal("Hello, world!", got.CleanedTranscript);
        Assert.Equal(1.5, got.AudioDurationSeconds);
    }

    [Fact]
    public void Recent_orders_newest_first()
    {
        _store.Upsert(new DictationRecord
        {
            Id = Guid.NewGuid(), StartedAt = DateTime.Now.AddMinutes(-5),
            RawTranscript = "older",
        });
        _store.Upsert(new DictationRecord
        {
            Id = Guid.NewGuid(), StartedAt = DateTime.Now,
            RawTranscript = "newer",
        });
        Assert.Equal("newer", _store.Recent(10)[0].RawTranscript);
    }

    [Fact]
    public void Fts_search_finds_transcripts()
    {
        _store.Upsert(new DictationRecord
        {
            Id = Guid.NewGuid(), StartedAt = DateTime.Now,
            RawTranscript = "the kubernetes deployment rolled out successfully",
            CleanedTranscript = "The Kubernetes deployment rolled out successfully.",
        });
        var hits = _store.Search("kubernetes");
        Assert.Single(hits);
    }

    [Fact]
    public void Timeline_json_survives_upserts_and_reads_null_on_legacy_rows()
    {
        var id = Guid.NewGuid();
        _store.Upsert(new DictationRecord { Id = id, StartedAt = DateTime.Now, Status = SessionStatus.Recording });
        var mid = _store.Get(id);
        Assert.Null(mid!.TimelineJson); // a take in flight — no timings yet

        _store.Upsert(new DictationRecord
        {
            Id = id, StartedAt = mid.StartedAt, Status = SessionStatus.Recorded,
            RawTranscript = "Hello world.", CleanedTranscript = "Hello world.",
            TimelineJson = "{\"Text\":\"Hello world.\",\"Words\":[{\"Start\":0.64,\"End\":0.88,\"Word\":\"Hello\"}]}",
        });
        var done = _store.Get(id);
        Assert.NotNull(done!.TimelineJson);
        Assert.Contains("Hello", done.TimelineJson);

        // A later row without a timeline must NOT erase it (COALESCE upsert —
        // the final no-timeline status write happens on every session).
        _store.Upsert(new DictationRecord
        {
            Id = id, StartedAt = mid.StartedAt, Status = SessionStatus.Inserted,
            RawTranscript = "Hello world.", CleanedTranscript = "Hello world.",
        });
        var final = _store.Get(id);
        Assert.NotNull(final!.TimelineJson);
        Assert.Equal(SessionStatus.Inserted, final.Status);
    }

    [Fact]
    public void Delete_all_clears_everything()
    {
        _store.Upsert(new DictationRecord
        {
            Id = Guid.NewGuid(), StartedAt = DateTime.Now, RawTranscript = "x",
        });
        _store.DeleteAll();
        Assert.Empty(_store.Recent(10));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_path); } catch { }
    }
}
