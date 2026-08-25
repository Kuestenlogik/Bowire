// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Reporting;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The shared wire shape of a rollup (#587).
/// </summary>
/// <remarks>
/// This projection exists because three surfaces emit the same document — the
/// REST endpoint, <c>bowire report rollup --json</c> and the
/// <c>bowire.report.rollup</c> MCP tool — and #364 showed what happens when
/// they each serialise the model themselves: the CLI emitted an enum ordinal
/// where the endpoint emitted a string, and a script could not treat them
/// alike. So the assertions here are about the document, not the method: the
/// spellings, and the distinction between "no report" and "a report saying
/// zero", which is the one thing flattening would quietly destroy.
/// </remarks>
public sealed class BowireRollupPayloadTests
{
    private static BowireRollup Rollup(params BowireServiceReport[] services)
        => new() { Services = services, Skipped = [] };

    private static JsonElement Wire(BowireRollup rollup)
        => JsonSerializer.SerializeToElement(BowireRollupPayload.ToWirePayload(rollup));

    private static JsonElement FirstService(BowireRollup rollup)
        => Wire(rollup).GetProperty("services")[0];

    [Fact]
    public void A_Service_With_No_Reports_Emits_Null_Sections_Not_Zeroes()
    {
        // The load-bearing distinction: "no lint report was read" and "lint
        // found nothing" are different statements, and a missing report must
        // not read as a clean bill of health.
        var svc = FirstService(Rollup(new BowireServiceReport { Service = "orders" }));

        Assert.Equal("orders", svc.GetProperty("service").GetString());
        Assert.Equal(JsonValueKind.Null, svc.GetProperty("lint").ValueKind);
        Assert.Equal(JsonValueKind.Null, svc.GetProperty("contracts").ValueKind);
        Assert.Equal(JsonValueKind.Null, svc.GetProperty("tests").ValueKind);
        Assert.Equal(JsonValueKind.Null, svc.GetProperty("benchmark").ValueKind);
        Assert.Equal(JsonValueKind.Null, svc.GetProperty("worst").ValueKind);
    }

    [Fact]
    public void A_Lint_Report_Finding_Nothing_Emits_Zeroes_Not_Null()
    {
        // The other half of the same distinction. One severity present is
        // enough to make the section real; the rest fill in as 0.
        var svc = FirstService(Rollup(new BowireServiceReport { Service = "orders", LintHigh = 0 }));

        var lint = svc.GetProperty("lint");
        Assert.Equal(JsonValueKind.Object, lint.ValueKind);
        Assert.Equal(0, lint.GetProperty("high").GetInt32());
        Assert.Equal(0, lint.GetProperty("medium").GetInt32());
        Assert.Equal(0, lint.GetProperty("low").GetInt32());
        Assert.Equal(0, lint.GetProperty("info").GetInt32());
    }

    [Fact]
    public void Counts_And_Severity_Are_Projected_As_Wire_Spellings()
    {
        var svc = FirstService(Rollup(new BowireServiceReport
        {
            Service = "orders",
            LintHigh = 2, LintMedium = 1,
            ContractsPassed = 7, ContractsTotal = 8,
            TestsPassed = 40, TestsTotal = 41,
            P95Ms = 12.5, BenchmarkPassed = true,
            ScanErrors = 3,
        }));

        // A string, not an ordinal — the whole point of the shared projection.
        Assert.Equal("high", svc.GetProperty("worst").GetString());
        Assert.Equal(2, svc.GetProperty("lint").GetProperty("high").GetInt32());
        Assert.Equal(7, svc.GetProperty("contracts").GetProperty("passed").GetInt32());
        Assert.Equal(8, svc.GetProperty("contracts").GetProperty("total").GetInt32());
        Assert.Equal(41, svc.GetProperty("tests").GetProperty("total").GetInt32());
        Assert.Equal(12.5, svc.GetProperty("benchmark").GetProperty("p95Ms").GetDouble());
        Assert.True(svc.GetProperty("benchmark").GetProperty("passed").GetBoolean());
        Assert.Equal(3, svc.GetProperty("scanErrors").GetInt32());
    }

    [Fact]
    public void A_Benchmark_Verdict_Alone_Is_Enough_To_Emit_The_Section()
    {
        // A k6 summary can carry a pass/fail without a p95, and a benchmark
        // run can report a p95 with no threshold to judge it against.
        var verdictOnly = FirstService(Rollup(new BowireServiceReport { Service = "a", BenchmarkPassed = false }));
        Assert.Equal(JsonValueKind.Object, verdictOnly.GetProperty("benchmark").ValueKind);
        Assert.Equal(JsonValueKind.Null, verdictOnly.GetProperty("benchmark").GetProperty("p95Ms").ValueKind);

        var latencyOnly = FirstService(Rollup(new BowireServiceReport { Service = "a", P95Ms = 9 }));
        Assert.Equal(JsonValueKind.Object, latencyOnly.GetProperty("benchmark").ValueKind);
        Assert.Equal(JsonValueKind.Null, latencyOnly.GetProperty("benchmark").GetProperty("passed").ValueKind);
    }

    [Fact]
    public void Sources_Travel_With_The_Row_So_A_Surprising_Number_Is_Traceable()
    {
        var report = new BowireServiceReport { Service = "orders", LintHigh = 1 };
        report.Sources.Add(new BowireReportSource(BowireReportKind.Lint, "reports/lint.json"));
        report.Sources.Add(new BowireReportSource(BowireReportKind.K6Summary, "reports/k6.json"));

        var sources = FirstService(Rollup(report)).GetProperty("sources");

        Assert.Equal(2, sources.GetArrayLength());
        Assert.Equal("lint", sources[0].GetProperty("kind").GetString());
        Assert.Equal("reports/lint.json", sources[0].GetProperty("path").GetString());
        // Hyphenated, not "K6Summary" — the enum name is not the wire name.
        Assert.Equal("k6-summary", sources[1].GetProperty("kind").GetString());
    }

    [Fact]
    public void The_Summary_Counts_Services_High_Clean_And_Skipped()
    {
        var rollup = new BowireRollup
        {
            Services =
            [
                new BowireServiceReport { Service = "a", LintHigh = 1 },
                new BowireServiceReport { Service = "b", LintLow = 2 },
                new BowireServiceReport { Service = "c" },
            ],
            Skipped = [new BowireReportSource(BowireReportKind.Sarif, "broken.json", "unparseable")],
        };

        var summary = Wire(rollup).GetProperty("summary");
        Assert.Equal(3, summary.GetProperty("services").GetInt32());
        Assert.Equal(1, summary.GetProperty("atHigh").GetInt32());
        // "clean" means nothing reportable at all — b has low findings, so it
        // is neither high nor clean.
        Assert.Equal(1, summary.GetProperty("clean").GetInt32());
        Assert.Equal(1, summary.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public void Skipped_Files_Carry_Their_Error_So_The_Gap_Is_Explained()
    {
        var rollup = new BowireRollup
        {
            Services = [],
            Skipped = [new BowireReportSource(BowireReportKind.Lint, "reports/bad.json", "unexpected token")],
        };

        var skipped = Wire(rollup).GetProperty("skipped");
        Assert.Equal("reports/bad.json", skipped[0].GetProperty("path").GetString());
        Assert.Equal("unexpected token", skipped[0].GetProperty("error").GetString());
    }

    [Fact]
    public void ToWirePayload_Rejects_A_Null_Rollup()
        => Assert.Throws<ArgumentNullException>(() => BowireRollupPayload.ToWirePayload(null!));

    [Theory]
    [InlineData(BowireRollupSeverity.High, "high")]
    [InlineData(BowireRollupSeverity.Medium, "medium")]
    [InlineData(BowireRollupSeverity.Low, "low")]
    [InlineData(BowireRollupSeverity.Info, "info")]
    public void SeverityText_Spells_Each_Level_In_Lower_Case(BowireRollupSeverity level, string expected)
        => Assert.Equal(expected, BowireRollupPayload.SeverityText(level));

    [Fact]
    public void SeverityText_Of_Nothing_Reportable_Is_Null()
        => Assert.Null(BowireRollupPayload.SeverityText(null));

    [Theory]
    [InlineData(BowireReportKind.Lint, "lint")]
    [InlineData(BowireReportKind.Contract, "contract")]
    [InlineData(BowireReportKind.Benchmark, "benchmark")]
    [InlineData(BowireReportKind.K6Summary, "k6-summary")]
    [InlineData(BowireReportKind.Sarif, "sarif")]
    [InlineData(BowireReportKind.JUnit, "junit")]
    public void KindText_Spells_Each_Report_Kind(BowireReportKind kind, string expected)
        => Assert.Equal(expected, BowireRollupPayload.KindText(kind));

    [Fact]
    public void KindText_Of_An_Unknown_Kind_Is_Named_Rather_Than_Blank()
        => Assert.Equal("unknown", BowireRollupPayload.KindText((BowireReportKind)999));

    // ---- the --fail-on gate ----
    //
    // A gate that quietly does nothing is worse than no gate: CI goes green
    // and nobody learns the flag was misspelled.

    [Theory]
    [InlineData(null, null)]
    [InlineData("none", null)]
    [InlineData("NONE", null)]
    [InlineData("info", BowireRollupSeverity.Info)]
    [InlineData("Low", BowireRollupSeverity.Low)]
    [InlineData("MEDIUM", BowireRollupSeverity.Medium)]
    [InlineData("  high  ", BowireRollupSeverity.High)]
    public void TryParseGate_Accepts_A_Level_In_Any_Casing(string? text, BowireRollupSeverity? expected)
    {
        Assert.True(BowireRollupPayload.TryParseGate(text, out var level, out var error));
        Assert.Equal(expected, level);
        Assert.Null(error);
    }

    [Fact]
    public void TryParseGate_Names_The_Typo_And_Lists_The_Valid_Levels()
    {
        Assert.False(BowireRollupPayload.TryParseGate("critical", out var level, out var error));
        Assert.Null(level);
        Assert.Contains("critical", error, StringComparison.Ordinal);
        Assert.Contains("none, info, low, medium or high", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CountAtOrAbove_Counts_The_Level_And_Everything_Worse()
    {
        var rollup = Rollup(
            new BowireServiceReport { Service = "a", LintHigh = 1 },
            new BowireServiceReport { Service = "b", LintMedium = 1 },
            new BowireServiceReport { Service = "c", LintInfo = 1 },
            new BowireServiceReport { Service = "d" });

        Assert.Equal(1, BowireRollupPayload.CountAtOrAbove(rollup, BowireRollupSeverity.High));
        Assert.Equal(2, BowireRollupPayload.CountAtOrAbove(rollup, BowireRollupSeverity.Medium));
        Assert.Equal(3, BowireRollupPayload.CountAtOrAbove(rollup, BowireRollupSeverity.Info));
    }

    [Fact]
    public void CountAtOrAbove_Rejects_A_Null_Rollup()
        => Assert.Throws<ArgumentNullException>(
            () => BowireRollupPayload.CountAtOrAbove(null!, BowireRollupSeverity.High));

    // ---- CLI table cells ----

    [Theory]
    [InlineData(null, null, "—")]
    [InlineData(3, null, "—")]      // no total means the report was never read
    [InlineData(null, 5, "0/5")]
    [InlineData(4, 5, "4/5")]
    public void Cell_Distinguishes_No_Report_From_Zero_Passing(int? passed, int? total, string expected)
        => Assert.Equal(expected, BowireRollupPayload.Cell(passed, total));

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0.5, "0.5ms")]
    [InlineData(12.345, "12.35ms")]   // sub-100: two decimals, because the
    [InlineData(99.994, "99.99ms")]   // difference between 12 and 12.35 matters
    [InlineData(100d, "100ms")]       // at/above 100 the decimals are noise
    [InlineData(1234.6, "1235ms")]
    public void Latency_Drops_Decimals_Once_They_Stop_Carrying_Information(double? ms, string expected)
        => Assert.Equal(expected, BowireRollupPayload.Latency(ms));
}
