// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Flows.Tests;

/// <summary>
/// #174 — data-source expansion. Covers the three sources (inline / CSV /
/// generator), the exactly-one-source contract, zero-row and row-cap
/// guards, CSV quoting + line-ending handling, seeded-random
/// reproducibility, and label resolution.
/// </summary>
public sealed class FlowDataSourceExpanderTests : IDisposable
{
    private readonly string _tempDir;

    public FlowDataSourceExpanderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static FlowDataSource Parse(string json) =>
        JsonSerializer.Deserialize<FlowDataSource>(json)!;

    // ---- source-selection contract ----

    [Fact]
    public void Expand_NoSource_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            FlowDataSourceExpander.Expand(new FlowDataSource(), "."));
        Assert.Contains("exactly one", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_TwoSources_Throws()
    {
        var data = Parse("""{ "inline": [ { "a": 1 } ], "csv": "x.csv" }""");
        var ex = Assert.Throws<InvalidDataException>(() =>
            FlowDataSourceExpander.Expand(data, "."));
        Assert.Contains("ambiguous", ex.Message, StringComparison.Ordinal);
    }

    // ---- inline ----

    [Fact]
    public void Inline_ScalarsSubstituteAsLiteralText()
    {
        var data = Parse("""{ "inline": [ { "id": 42, "name": "Ada", "ok": true } ] }""");
        var rows = FlowDataSourceExpander.Expand(data, ".");
        var row = Assert.Single(rows);
        Assert.Equal("42", row.Values["id"]);
        Assert.Equal("Ada", row.Values["name"]);
        Assert.Equal("true", row.Values["ok"]);
    }

    [Fact]
    public void Inline_NestedStructuresSubstituteAsCompactJson()
    {
        var data = Parse("""{ "inline": [ { "tags": [ "a", "b" ], "meta": { "k": 1 } } ] }""");
        var rows = FlowDataSourceExpander.Expand(data, ".");
        Assert.Equal("""["a","b"]""", rows[0].Values["tags"]);
        Assert.Equal("""{"k":1}""", rows[0].Values["meta"]);
    }

    [Fact]
    public void Inline_NullValue_BecomesEmptyString()
    {
        var data = Parse("""{ "inline": [ { "gone": null } ] }""");
        var rows = FlowDataSourceExpander.Expand(data, ".");
        Assert.Equal(string.Empty, rows[0].Values["gone"]);
    }

    [Fact]
    public void Inline_EmptyArray_Throws()
    {
        var data = Parse("""{ "inline": [] }""");
        var ex = Assert.Throws<InvalidDataException>(() =>
            FlowDataSourceExpander.Expand(data, "."));
        Assert.Contains("no rows", ex.Message, StringComparison.Ordinal);
    }

    // ---- CSV ----

    private string WriteCsv(string content)
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, content);
        return path;
    }

    private FlowDataSource CsvSource(string content) => new()
    {
        Csv = Path.GetFileName(WriteCsv(content)),
    };

    [Fact]
    public void Csv_HeaderNamesColumns_RowsMapByHeader()
    {
        var data = CsvSource("userId,name\n1,Ada\n2,Grace\n");
        var rows = FlowDataSourceExpander.Expand(data, _tempDir);
        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0].Values["userId"]);
        Assert.Equal("Grace", rows[1].Values["name"]);
    }

    [Fact]
    public void Csv_QuotedFields_CommaNewlineAndEscapedQuoteSurvive()
    {
        var data = CsvSource("text\n\"a,b\"\n\"line1\nline2\"\n\"she said \"\"hi\"\"\"\n");
        var rows = FlowDataSourceExpander.Expand(data, _tempDir);
        Assert.Equal("a,b", rows[0].Values["text"]);
        Assert.Equal("line1\nline2", rows[1].Values["text"]);
        Assert.Equal("she said \"hi\"", rows[2].Values["text"]);
    }

    [Fact]
    public void Csv_CrlfAndMissingTrailingNewline_BothAccepted()
    {
        var data = CsvSource("a,b\r\n1,2\r\n3,4");
        var rows = FlowDataSourceExpander.Expand(data, _tempDir);
        Assert.Equal(2, rows.Count);
        Assert.Equal("4", rows[1].Values["b"]);
    }

    [Fact]
    public void Csv_FieldCountMismatch_ThrowsWithRowNumber()
    {
        var data = CsvSource("a,b\n1\n");
        var ex = Assert.Throws<InvalidDataException>(() =>
            FlowDataSourceExpander.Expand(data, _tempDir));
        Assert.Contains("row 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_UnterminatedQuote_Throws()
    {
        var data = CsvSource("a\n\"open\n");
        Assert.Throws<InvalidDataException>(() =>
            FlowDataSourceExpander.Expand(data, _tempDir));
    }

    [Fact]
    public void Csv_EmptyFile_Throws()
    {
        var data = CsvSource(string.Empty);
        var ex = Assert.Throws<InvalidDataException>(() =>
            FlowDataSourceExpander.Expand(data, _tempDir));
        Assert.Contains("header", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_HeaderOnly_ThrowsZeroRows()
    {
        var data = CsvSource("a,b\n");
        var ex = Assert.Throws<InvalidDataException>(() =>
            FlowDataSourceExpander.Expand(data, _tempDir));
        Assert.Contains("no rows", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_MissingFile_LetsIoExceptionPropagate()
    {
        var data = new FlowDataSource { Csv = "absent.csv" };
        Assert.ThrowsAny<IOException>(() =>
            FlowDataSourceExpander.Expand(data, _tempDir));
    }

    // ---- generator: range ----

    [Fact]
    public void Range_InclusiveBothEnds()
    {
        var data = Parse("""{ "generator": { "kind": "range", "var": "i", "from": 1, "to": 3 } }""");
        var rows = FlowDataSourceExpander.Expand(data, ".");
        Assert.Equal(["1", "2", "3"], rows.Select(r => r.Values["i"]));
    }

    [Fact]
    public void Range_SingleValue_OneRow()
    {
        var data = Parse("""{ "generator": { "kind": "range", "var": "i", "from": 7, "to": 7 } }""");
        var row = Assert.Single(FlowDataSourceExpander.Expand(data, "."));
        Assert.Equal("7", row.Values["i"]);
    }

    [Fact]
    public void Range_Inverted_Throws()
    {
        var data = Parse("""{ "generator": { "kind": "range", "var": "i", "from": 5, "to": 1 } }""");
        var ex = Assert.Throws<InvalidDataException>(() =>
            FlowDataSourceExpander.Expand(data, "."));
        Assert.Contains("inverted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Range_BeyondCap_Throws()
    {
        var data = Parse("""{ "generator": { "kind": "range", "var": "i", "from": 0, "to": 2000000000 } }""");
        var ex = Assert.Throws<InvalidDataException>(() =>
            FlowDataSourceExpander.Expand(data, "."));
        Assert.Contains("caps", ex.Message, StringComparison.Ordinal);
    }

    // ---- generator: random ----

    [Fact]
    public void Random_SameSeed_SameRows()
    {
        const string json = """{ "generator": { "kind": "random", "var": "n", "count": 20, "seed": 42, "min": 1, "max": 6 } }""";
        var first = FlowDataSourceExpander.Expand(Parse(json), ".");
        var second = FlowDataSourceExpander.Expand(Parse(json), ".");
        Assert.Equal(
            first.Select(r => r.Values["n"]),
            second.Select(r => r.Values["n"]));
    }

    [Fact]
    public void Random_ValuesStayWithinInclusiveBounds()
    {
        var data = Parse("""{ "generator": { "kind": "random", "var": "n", "count": 200, "seed": 7, "min": -3, "max": 3 } }""");
        var rows = FlowDataSourceExpander.Expand(data, ".");
        Assert.Equal(200, rows.Count);
        Assert.All(rows, r =>
        {
            var v = long.Parse(r.Values["n"], System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(v, -3, 3);
        });
    }

    [Fact]
    public void Random_DifferentSeeds_DifferentSequences()
    {
        var a = FlowDataSourceExpander.Expand(
            Parse("""{ "generator": { "kind": "random", "var": "n", "count": 20, "seed": 1, "min": 0, "max": 1000000 } }"""), ".");
        var b = FlowDataSourceExpander.Expand(
            Parse("""{ "generator": { "kind": "random", "var": "n", "count": 20, "seed": 2, "min": 0, "max": 1000000 } }"""), ".");
        Assert.NotEqual(
            a.Select(r => r.Values["n"]),
            b.Select(r => r.Values["n"]));
    }

    [Fact]
    public void Random_CountZero_Throws()
    {
        var data = Parse("""{ "generator": { "kind": "random", "var": "n", "count": 0, "seed": 1 } }""");
        Assert.Throws<InvalidDataException>(() =>
            FlowDataSourceExpander.Expand(data, "."));
    }

    [Fact]
    public void Random_InvertedBounds_Throws()
    {
        var data = Parse("""{ "generator": { "kind": "random", "var": "n", "count": 5, "seed": 1, "min": 10, "max": 2 } }""");
        var ex = Assert.Throws<InvalidDataException>(() =>
            FlowDataSourceExpander.Expand(data, "."));
        Assert.Contains("inverted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_UnknownKind_Throws()
    {
        var data = Parse("""{ "generator": { "kind": "fibonacci", "var": "n" } }""");
        var ex = Assert.Throws<InvalidDataException>(() =>
            FlowDataSourceExpander.Expand(data, "."));
        Assert.Contains("fibonacci", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmptyVar_Throws()
    {
        var data = Parse("""{ "generator": { "kind": "range", "var": "", "from": 1, "to": 2 } }""");
        Assert.Throws<InvalidDataException>(() =>
            FlowDataSourceExpander.Expand(data, "."));
    }

    // ---- labels ----

    [Fact]
    public void Label_DefaultsToZeroBasedIndex()
    {
        var data = Parse("""{ "inline": [ { "a": 1 }, { "a": 2 } ] }""");
        var rows = FlowDataSourceExpander.Expand(data, ".");
        Assert.Equal(["0", "1"], rows.Select(r => r.Label));
    }

    [Fact]
    public void Label_UsesLabelColumnWhenPresent()
    {
        var data = Parse("""{ "inline": [ { "name": "Ada" }, { "name": "Grace" } ], "labelColumn": "name" }""");
        var rows = FlowDataSourceExpander.Expand(data, ".");
        Assert.Equal(["Ada", "Grace"], rows.Select(r => r.Label));
    }

    [Fact]
    public void Label_MissingOrEmptyColumnValue_FallsBackToIndex()
    {
        var data = Parse("""{ "inline": [ { "name": "" }, { "other": "x" } ], "labelColumn": "name" }""");
        var rows = FlowDataSourceExpander.Expand(data, ".");
        Assert.Equal(["0", "1"], rows.Select(r => r.Label));
    }
}
