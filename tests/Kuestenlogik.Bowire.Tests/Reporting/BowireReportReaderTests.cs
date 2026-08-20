// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Reporting;

namespace Kuestenlogik.Bowire.Tests.Reporting;

/// <summary>
/// #587 — the rollup reader. It parses the artefact FORMATS rather than the
/// producing packages' types, because the reports come from other repos and
/// other Bowire versions; these fix the shapes it must recognise and the
/// aggregation rules a platform team then reads off the table.
/// </summary>
public sealed class BowireReportReaderTests : IDisposable
{
    private readonly string _root;

    public BowireReportReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "bowire-rollup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private Task<BowireRollup> ReadAsync(string? serviceOverride = null)
        => BowireReportReader.ReadAsync([_root], serviceOverride, TestContext.Current.CancellationToken);

    // ---- format recognition -------------------------------------------

    [Fact]
    public async Task Lint_FindingsAreCountedBySeverity()
    {
        Write("orders-api/lint.json", """
        {
          "findings": [
            { "severity": "High", "service": "Orders", "message": "secret in response" },
            { "severity": "Medium", "service": "Orders", "message": "no pagination" },
            { "severity": "Low", "service": "Orders", "message": "string timestamp" },
            { "severity": "Info", "service": "Orders", "message": "note" }
          ],
          "summary": { "total": 4 }
        }
        """);

        var rollup = await ReadAsync();

        var row = Assert.Single(rollup.Services);
        Assert.Equal(1, row.LintHigh);
        Assert.Equal(1, row.LintMedium);
        Assert.Equal(1, row.LintLow);
        Assert.Equal(1, row.LintInfo);
        Assert.Equal(BowireRollupSeverity.High, row.Worst);
    }

    [Fact]
    public async Task Lint_SingleServiceReportAdoptsTheFindingsServiceName()
    {
        // The report knows whose surface it linted; that beats guessing from
        // the directory.
        Write("dropped-here/lint.json", """
        { "findings": [ { "severity": "Low", "service": "Billing", "message": "x" } ] }
        """);

        var rollup = await ReadAsync();

        Assert.Equal("Billing", Assert.Single(rollup.Services).Service);
    }

    [Fact]
    public async Task Lint_MixedServiceReportFallsBackToThePath()
    {
        // Findings across several services can't name one owner, so the path
        // decides — otherwise an arbitrary finding would win.
        Write("gateway/lint.json", """
        {
          "findings": [
            { "severity": "Low", "service": "A", "message": "x" },
            { "severity": "Low", "service": "B", "message": "y" }
          ]
        }
        """);

        var rollup = await ReadAsync();

        Assert.Equal("gateway", Assert.Single(rollup.Services).Service);
    }

    [Fact]
    public async Task Contract_CountsPassAndFailAndFilesUnderTheProvider()
    {
        Write("anywhere/held.json", """
        { "consumer": "web", "provider": "orders-api", "startedAt": "2026-08-01T10:00:00Z",
          "failedInteractions": 0, "interactions": [ { "passed": true } ] }
        """);
        Write("anywhere/broken.json", """
        { "consumer": "mobile", "provider": "orders-api", "startedAt": "2026-08-02T10:00:00Z",
          "failedInteractions": 1, "interactions": [ { "passed": false } ] }
        """);

        var rollup = await ReadAsync();

        // Both contracts are about the same provider — that is the service
        // whose rollup row a broken contract belongs in.
        var row = Assert.Single(rollup.Services);
        Assert.Equal("orders-api", row.Service);
        Assert.Equal(2, row.ContractsTotal);
        Assert.Equal(1, row.ContractsPassed);
        Assert.Equal(BowireRollupSeverity.High, row.Worst);
        Assert.Equal(new DateTime(2026, 8, 2, 10, 0, 0, DateTimeKind.Utc), row.LastReportAt);
    }

    [Fact]
    public async Task Benchmark_TakesTheNewestRunFromAHistory()
    {
        Write("orders-api/nightly.runs.json", """
        [
          { "scheduleId": "nightly", "startedAt": "2026-08-02T03:00:00Z", "p95": 210.0, "passed": false },
          { "scheduleId": "nightly", "startedAt": "2026-08-01T03:00:00Z", "p95": 90.0, "passed": true }
        ]
        """);

        var rollup = await ReadAsync();

        var row = Assert.Single(rollup.Services);
        Assert.Equal(210.0, row.P95Ms);
        Assert.False(row.BenchmarkPassed);
        Assert.Equal(BowireRollupSeverity.Medium, row.Worst);
    }

    [Fact]
    public async Task K6Summary_ReadsP95AndThresholdVerdicts()
    {
        Write("orders-api/k6.json", """
        {
          "metrics": {
            "http_req_duration": {
              "type": "trend",
              "values": { "p(95)": 142.5 },
              "thresholds": { "p95<100": { "ok": false } }
            }
          }
        }
        """);

        var rollup = await ReadAsync();

        var row = Assert.Single(rollup.Services);
        Assert.Equal(142.5, row.P95Ms);
        Assert.False(row.BenchmarkPassed);
    }

    [Fact]
    public async Task Sarif_CountsErrorLevelResultsOnly()
    {
        Write("orders-api/scan.sarif", """
        { "runs": [ { "results": [
            { "level": "error" }, { "level": "error" }, { "level": "warning" }, { "level": "note" }
        ] } ] }
        """);

        var rollup = await ReadAsync();

        var row = Assert.Single(rollup.Services);
        Assert.Equal(2, row.ScanErrors);
        Assert.Equal(BowireRollupSeverity.High, row.Worst);
    }

    [Fact]
    public async Task JUnit_CountsTestsAndFoldsErrorsIntoFailures()
    {
        Write("orders-api/test.xml", """
        <?xml version="1.0"?>
        <testsuites>
          <testsuite name="s" tests="10" failures="1" errors="1" />
        </testsuites>
        """);

        var rollup = await ReadAsync();

        var row = Assert.Single(rollup.Services);
        Assert.Equal(10, row.TestsTotal);
        Assert.Equal(8, row.TestsPassed);
        Assert.Equal(BowireRollupSeverity.High, row.Worst);
    }

    // ---- aggregation across artefacts ---------------------------------

    [Fact]
    public async Task OneServiceGathersEveryArtefactKind()
    {
        Write("orders-api/lint.json", """{ "findings": [ { "severity": "Low", "message": "x" } ] }""");
        Write("orders-api/contract.json", """
        { "consumer": "web", "provider": "orders-api", "failedInteractions": 0, "interactions": [] }
        """);
        Write("orders-api/test.xml", """<testsuites><testsuite tests="3" failures="0" /></testsuites>""");

        var rollup = await ReadAsync();

        var row = Assert.Single(rollup.Services);
        Assert.Equal(1, row.LintLow);
        Assert.Equal(1, row.ContractsTotal);
        Assert.Equal(3, row.TestsTotal);
        Assert.Equal(3, row.Sources.Count);
    }

    [Fact]
    public async Task ServicesAreSeparatedAndSorted()
    {
        Write("zulu/lint.json", """{ "findings": [] }""");
        Write("alpha/lint.json", """{ "findings": [] }""");

        var rollup = await ReadAsync();

        Assert.Equal(["alpha", "zulu"], rollup.Services.Select(s => s.Service));
    }

    [Fact]
    public async Task MissingReportIsNotTheSameAsACleanOne()
    {
        // A service with no lint report must leave the counts null, so a
        // reader cannot mistake "never linted" for "linted, nothing found".
        Write("orders-api/test.xml", """<testsuites><testsuite tests="1" failures="0" /></testsuites>""");

        var rollup = await ReadAsync();

        var row = Assert.Single(rollup.Services);
        Assert.Null(row.LintHigh);
        Assert.Null(row.ContractsTotal);
        Assert.Null(row.P95Ms);
        Assert.Null(row.Worst);   // nothing reportable
    }

    // ---- robustness ---------------------------------------------------

    [Fact]
    public async Task UnrecognisedAndMalformedFilesAreSkippedNotFatal()
    {
        Write("orders-api/lint.json", """{ "findings": [ { "severity": "High", "message": "x" } ] }""");
        Write("orders-api/random.json", """{ "hello": "world" }""");
        Write("orders-api/broken.json", "{ not json");

        var rollup = await ReadAsync();

        Assert.Single(rollup.Services);
        Assert.Equal(1, rollup.Services[0].LintHigh);
        Assert.Equal(2, rollup.Skipped.Count);
        Assert.All(rollup.Skipped, s => Assert.NotNull(s.Error));
    }

    [Fact]
    public async Task MissingRootIsAnEmptyRollupNotAnError()
    {
        var rollup = await BowireReportReader.ReadAsync(
            [Path.Combine(_root, "nope")], null, TestContext.Current.CancellationToken);

        Assert.Empty(rollup.Services);
        Assert.Empty(rollup.Skipped);
    }

    [Fact]
    public async Task ServiceOverrideWinsOverPathAndReportHints()
    {
        Write("orders-api/lint.json", """{ "findings": [ { "severity": "Low", "service": "Billing", "message": "x" } ] }""");

        var rollup = await ReadAsync(serviceOverride: "forced");

        Assert.Equal("forced", Assert.Single(rollup.Services).Service);
    }

    [Theory]
    // Storage-layout directories are skipped so `.bowire/contract-results/x.json`
    // doesn't produce a service literally called "contract-results".
    [InlineData("orders-api/lint.json", "orders-api")]
    [InlineData(".bowire/contract-results/a__b.json", "a__b")]
    [InlineData("reports/orders-api/lint.json", "orders-api")]
    [InlineData("loose.json", "loose")]
    public void ServiceFromPath_SkipsStorageLayout(string relative, string expected)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(expected, BowireReportReader.ServiceFromPath(full, _root));
    }

    [Fact]
    public async Task SummaryCountsServicesByWorstSeverity()
    {
        Write("bad/lint.json", """{ "findings": [ { "severity": "High", "message": "x" } ] }""");
        Write("meh/lint.json", """{ "findings": [ { "severity": "Medium", "message": "x" } ] }""");
        Write("clean/lint.json", """{ "findings": [] }""");

        var rollup = await ReadAsync();

        Assert.Equal(3, rollup.Services.Count);
        Assert.Equal(1, rollup.ServicesAtHigh);
        Assert.Equal(1, rollup.ServicesClean);
    }
}
