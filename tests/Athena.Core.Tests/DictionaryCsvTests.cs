// Tests for the dictionary CSV boundary: quote-aware parsing, the
// spreadsheet formula-injection guard, and exact export→import round-trips.
// The dictionary rides into the cleanup prompt and is enforced after the
// model, so what leaves the app must come back identical.

using Athena.Core;
using Xunit;

namespace Athena.Core.Tests;

public class DictionaryCsvTests : IDisposable
{
    private readonly string _dir;
    private readonly DictionaryStore _store;

    public DictionaryCsvTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "athena-dict-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = DictionaryStore.LoadForTest(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private DictionaryStore FreshStore(string name)
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        return DictionaryStore.LoadForTest(d);
    }

    [Fact]
    public void Export_import_round_trips_commas_quotes_and_spaces()
    {
        _store.AddRule("cooper netties, \"the pod tool\"", "Kubernetes");
        _store.AddRule("jira dash board", "Jira dashboard");
        _store.AddTerm("k8s");

        var other = FreshStore("roundtrip");
        other.ImportCsv(_store.ExportCsv());

        var expected = _store.Snapshot();
        var actual = other.Snapshot();
        Assert.Equal(expected.Terms.Select(t => t.Term), actual.Terms.Select(t => t.Term));
        Assert.Equal(
            expected.Replacements.Select(r => (r.Wrong, r.Right)),
            actual.Replacements.Select(r => (r.Wrong, r.Right)));
    }

    [Fact]
    public void Formula_leading_cells_are_guarded_on_export_and_unguarded_on_import()
    {
        _store.AddTerm("- Robert");      // leading minus
        _store.AddRule("=cmd summary", "command summary"); // leading equals

        var csv = _store.ExportCsv();

        // The guard is literally present in the emitted file…
        Assert.Contains("'- Robert", csv);
        Assert.Contains("'=cmd summary", csv);

        // …and a fresh import reproduces the original data exactly.
        var other = FreshStore("unguard");
        other.ImportCsv(csv);
        Assert.Contains(other.Snapshot().Terms, t => t.Term == "- Robert");
        Assert.Contains(other.Snapshot().Replacements, r => r.Wrong == "=cmd summary");
    }

    [Fact]
    public void Imported_formula_cells_are_guarded_on_reexport()
    {
        var other = FreshStore("hostile");
        // A hostile import stores the text verbatim (the dictionary is data),
        // but the guard re-applies the moment it leaves the app again. The cell
        // is properly CSV-quoted (a quoted cell starts with "), with the inner
        // quotes doubled.
        other.ImportCsv("\"=HYPERLINK(\"\"http://evil.example\"\",\"\"click\"\"),safe");

        var reexported = other.ExportCsv();
        Assert.Contains("'=HYPERLINK(", reexported);

        var third = FreshStore("hostile2");
        third.ImportCsv(reexported);
        // One quoted cell (its commas live inside the quotes) → one term.
        Assert.Contains(third.Snapshot().Terms, t => t.Term == "=HYPERLINK(\"http://evil.example\",\"click\"),safe");
    }

    [Fact]
    public void Quoted_fields_with_commas_parse_as_one_field()
    {
        var other = FreshStore("quoted");
        other.ImportCsv("\"wrong, with comma\",\"right, also comma\"");
        var snap = other.Snapshot();
        var rule = Assert.Single(snap.Replacements);
        Assert.Equal("wrong, with comma", rule.Wrong);
        Assert.Equal("right, also comma", rule.Right);
    }

    [Fact]
    public void Plain_unquoted_lines_import_as_before()
    {
        var other = FreshStore("plain");
        other.ImportCsv("cooper netties,Kubernetes\nk8s\n");
        var snap = other.Snapshot();
        Assert.Contains(snap.Replacements, r => r.Wrong == "cooper netties" && r.Right == "Kubernetes");
        Assert.Contains(snap.Terms, t => t.Term == "k8s");
    }
}
