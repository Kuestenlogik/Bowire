// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Flows.Expectations;

namespace Kuestenlogik.Bowire.Flows.Tests;

/// <summary>
/// #171 — pure-function snapshot comparison. Covers exact vs structural
/// modes, shape drift (missing / extra / kind-changed members), array
/// length semantics per mode, dynamic-field ignore markers (value-only —
/// kind changes still fail), wildcard segments, and non-JSON bodies.
/// </summary>
public class FlowSnapshotComparerTests
{
    // Ignore-path fixtures as static readonly fields (CA1861-clean).
    private static readonly string[] IgnoreTs = ["$.ts"];
    private static readonly string[] IgnoreItemIds = ["$.items.*.id"];

    [Fact]
    public void Exact_IdenticalJson_NoDiffs()
    {
        var diffs = FlowSnapshotComparer.Compare(
            """{ "a": 1, "b": "x" }""", """{ "b": "x", "a": 1 }""", FlowSnapshotMode.Exact);
        Assert.Empty(diffs);
    }

    [Fact]
    public void Exact_ValueDrift_ReportsPathAndBothValues()
    {
        var diffs = FlowSnapshotComparer.Compare(
            """{ "a": 1 }""", """{ "a": 2 }""", FlowSnapshotMode.Exact);
        var d = Assert.Single(diffs);
        Assert.Contains("$.a", d, StringComparison.Ordinal);
        Assert.Contains("1", d, StringComparison.Ordinal);
        Assert.Contains("2", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_MissingAndExtraKeys_BothReported()
    {
        var diffs = FlowSnapshotComparer.Compare(
            """{ "gone": 1, "kept": 2 }""", """{ "kept": 2, "added": 3 }""", FlowSnapshotMode.Exact);
        Assert.Equal(2, diffs.Count);
        Assert.Contains(diffs, d => d.Contains("$.gone", StringComparison.Ordinal) && d.Contains("missing", StringComparison.Ordinal));
        Assert.Contains(diffs, d => d.Contains("$.added", StringComparison.Ordinal) && d.Contains("unexpected", StringComparison.Ordinal));
    }

    [Fact]
    public void Exact_ArrayLengthChange_Fails_Structural_Passes()
    {
        const string baseline = """{ "items": [ { "id": 1 }, { "id": 2 } ] }""";
        const string actual = """{ "items": [ { "id": 9 } ] }""";

        var exact = FlowSnapshotComparer.Compare(baseline, actual, FlowSnapshotMode.Exact);
        Assert.Contains(exact, d => d.Contains("array length", StringComparison.Ordinal));

        var structural = FlowSnapshotComparer.Compare(baseline, actual, FlowSnapshotMode.Structural);
        Assert.Empty(structural); // shape holds; values + length may vary
    }

    [Fact]
    public void Structural_ValueChangesPass_KindChangesFail()
    {
        const string baseline = """{ "n": 1, "s": "a" }""";

        Assert.Empty(FlowSnapshotComparer.Compare(
            baseline, """{ "n": 999, "s": "zzz" }""", FlowSnapshotMode.Structural));

        var diffs = FlowSnapshotComparer.Compare(
            baseline, """{ "n": "1", "s": "a" }""", FlowSnapshotMode.Structural);
        var d = Assert.Single(diffs);
        Assert.Contains("kind changed", d, StringComparison.Ordinal);
        Assert.Contains("number", d, StringComparison.Ordinal);
        Assert.Contains("string", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Ignore_ValueChangeOnMarkedPath_Passes()
    {
        var diffs = FlowSnapshotComparer.Compare(
            """{ "id": "abc", "ts": 111 }""",
            """{ "id": "abc", "ts": 999 }""",
            FlowSnapshotMode.Exact,
            IgnoreTs);
        Assert.Empty(diffs);
    }

    [Fact]
    public void Ignore_KindChangeOnMarkedPath_StillFails()
    {
        // "ignore-value, keep-shape": the marker exempts the VALUE only.
        var diffs = FlowSnapshotComparer.Compare(
            """{ "ts": 111 }""", """{ "ts": "2026-07-03" }""",
            FlowSnapshotMode.Exact, IgnoreTs);
        var d = Assert.Single(diffs);
        Assert.Contains("kind changed", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Ignore_WildcardSegment_MatchesEveryArrayIndex()
    {
        var diffs = FlowSnapshotComparer.Compare(
            """{ "items": [ { "id": 1, "v": "a" }, { "id": 2, "v": "b" } ] }""",
            """{ "items": [ { "id": 7, "v": "a" }, { "id": 8, "v": "b" } ] }""",
            FlowSnapshotMode.Exact,
            IgnoreItemIds);
        Assert.Empty(diffs);
    }

    [Fact]
    public void NonJson_Exact_OrdinalCompare()
    {
        Assert.Empty(FlowSnapshotComparer.Compare("plain text", "plain text", FlowSnapshotMode.Exact));
        var diffs = FlowSnapshotComparer.Compare("plain text", "other text", FlowSnapshotMode.Exact);
        Assert.Single(diffs);
    }

    [Fact]
    public void JsonVsNonJson_AlwaysDiffs()
    {
        var diffs = FlowSnapshotComparer.Compare("""{ "a": 1 }""", "not json", FlowSnapshotMode.Structural);
        var d = Assert.Single(diffs);
        Assert.Contains("one side is JSON", d, StringComparison.Ordinal);
    }

    [Fact]
    public void DiffCount_CappedWithRollupLine()
    {
        // 26 drifted leaves → 10 detail lines + 1 rollup.
        var baseline = "{" + string.Join(",", Enumerable.Range(0, 26).Select(i => $"\"k{i}\": 0")) + "}";
        var actual = "{" + string.Join(",", Enumerable.Range(0, 26).Select(i => $"\"k{i}\": 1")) + "}";
        var diffs = FlowSnapshotComparer.Compare(baseline, actual, FlowSnapshotMode.Exact);
        Assert.Equal(11, diffs.Count);
        Assert.Contains("more difference", diffs[^1], StringComparison.Ordinal);
    }
}
