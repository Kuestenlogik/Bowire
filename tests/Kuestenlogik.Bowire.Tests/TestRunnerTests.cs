// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests <see cref="TestRunner.RunAsync"/>'s pre-invocation guards —
/// the paths that fail before any protocol call goes out (missing
/// path, missing file, empty / malformed JSON). The happy-path
/// "runs against a real server" is covered by the integration harness;
/// here we just keep the diagnostic exit codes (2 for usage, …)
/// honest so CI scripts can rely on them.
/// </summary>
public sealed class TestRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public TestRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-tr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_NullCli_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => TestRunner.RunAsync(null!));
    }

    [Fact]
    public async Task RunAsync_NoCollectionPath_ReturnsUsageExit()
    {
        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = null });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_EmptyCollectionPath_ReturnsUsageExit()
    {
        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = "" });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_MissingFile_ReturnsUsageExit()
    {
        var path = Path.Combine(_tempDir, "absent.json");
        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_MalformedJson_ReturnsUsageExit()
    {
        var path = Path.Combine(_tempDir, "broken.json");
        await File.WriteAllTextAsync(path, "{ this is not json", TestContext.Current.CancellationToken);
        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_EmptyTestsArray_ReturnsUsageExit()
    {
        var path = Path.Combine(_tempDir, "empty.json");
        await File.WriteAllTextAsync(path, """{ "name": "x", "tests": [] }""", TestContext.Current.CancellationToken);
        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_NullTestsField_ReturnsUsageExit()
    {
        // tests: null deserialises to a null List<TestEntry>, the runner
        // surfaces the same "no tests" diagnostic as an explicit empty
        // array.
        var path = Path.Combine(_tempDir, "null-tests.json");
        await File.WriteAllTextAsync(path, """{ "name": "x", "tests": null }""", TestContext.Current.CancellationToken);
        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_TestMissingService_ReportsErrorAndExitsOne()
    {
        // Test entry has a name + method but no service → RunTestAsync
        // sets result.Error before any protocol call. Whole run is a
        // single failed test → exit 1.
        var path = Path.Combine(_tempDir, "no-service.json");
        await File.WriteAllTextAsync(path, """
            {
              "name": "x",
              "tests": [
                { "name": "broken", "method": "Get" }
              ]
            }
            """, TestContext.Current.CancellationToken);

        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task RunAsync_UnknownProtocolId_ReportsErrorAndExitsOne()
    {
        // protocol id that no plugin registers → RunTestAsync hits
        // "Protocol '...' not registered". Exercises the env merge +
        // ${var} substitution path (both feed into the message
        // pipeline before the protocol check).
        var path = Path.Combine(_tempDir, "unknown-proto.json");
        await File.WriteAllTextAsync(path, """
            {
              "name": "env-merge-coverage",
              "protocol": "no-such-protocol-xyz",
              "serverUrl": "http://localhost:1",
              "environment": { "userId": "42", "host": "from-collection" },
              "tests": [
                {
                  "name": "with-vars",
                  "service": "users",
                  "method": "Get",
                  "environment": { "host": "from-test" },
                  "messages": ["{\"id\": ${userId}, \"host\": \"${host}\", \"unknown\": \"${missing}\"}"],
                  "metadata": { "x-host": "${host}" }
                }
              ]
            }
            """, TestContext.Current.CancellationToken);

        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task RunAsync_UnknownProtocolWithReports_WritesHtmlAndJUnit()
    {
        // Unknown protocol → guaranteed "Protocol not registered" failure
        // (no network race). Verifies both --report and --junit files
        // are written, exercising those branches in RunAsync proper.
        var coll = Path.Combine(_tempDir, "with-reports.json");
        var report = Path.Combine(_tempDir, "report.html");
        var junit = Path.Combine(_tempDir, "junit.xml");
        await File.WriteAllTextAsync(coll, """
            {
              "name": "rep",
              "protocol": "no-such-protocol-zzz",
              "serverUrl": "http://localhost:1",
              "tests": [
                { "name": "ping", "service": "Health", "method": "Get" }
              ]
            }
            """, TestContext.Current.CancellationToken);

        var rc = await TestRunner.RunAsync(new TestCliOptions
        {
            CollectionPath = coll,
            ReportPath = report,
            JUnitPath = junit,
        });

        Assert.Equal(1, rc);
        Assert.True(File.Exists(report));
        Assert.True(File.Exists(junit));
        var html = await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken);
        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        var xml = await File.ReadAllTextAsync(junit, TestContext.Current.CancellationToken);
        Assert.Contains("<testsuite", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_TestMissingMethod_ReportsErrorAndExitsOne()
    {
        // Symmetric companion to the missing-service case — only service
        // is set, no method. Hits the same early-return guard via the
        // other half of the predicate.
        var path = Path.Combine(_tempDir, "no-method.json");
        await File.WriteAllTextAsync(path, """
            {
              "name": "x",
              "tests": [
                { "name": "broken", "service": "Health" }
              ]
            }
            """, TestContext.Current.CancellationToken);

        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task RunAsync_TestNameDefaultsToServiceSlashMethod()
    {
        // Test entry without a "name" field falls back to "service/method"
        // for display + report rendering. We hit that branch via the
        // unknown-protocol error path so the deterministic exit-1 is
        // reached without a network call.
        var coll = Path.Combine(_tempDir, "no-name.json");
        await File.WriteAllTextAsync(coll, """
            {
              "name": "no-name-coll",
              "protocol": "no-such-protocol-yyy",
              "serverUrl": "http://localhost:1",
              "tests": [
                { "service": "Health", "method": "Ping" }
              ]
            }
            """, TestContext.Current.CancellationToken);

        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = coll });
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task RunAsync_ReportPathPointsAtUnwritableLocation_ContinuesPastFailure()
    {
        // ReportPath under a directory that can't be created (a regular
        // file used as a parent) → the report write throws, the runner
        // catches and reports "Failed to write report" but keeps going.
        // Exit code still reflects the underlying test outcome.
        var coll = Path.Combine(_tempDir, "blocked.json");
        await File.WriteAllTextAsync(coll, """
            {
              "name": "blocked-report",
              "protocol": "no-such-protocol",
              "serverUrl": "http://localhost:1",
              "tests": [
                { "name": "t", "service": "s", "method": "m" }
              ]
            }
            """, TestContext.Current.CancellationToken);

        // Use a regular file as the "parent" so File.WriteAllTextAsync
        // fails when it tries to create the report under it.
        var blockingFile = Path.Combine(_tempDir, "as-parent");
        await File.WriteAllTextAsync(blockingFile, "x", TestContext.Current.CancellationToken);
        var report = Path.Combine(blockingFile, "report.html");
        var junit = Path.Combine(blockingFile, "junit.xml");

        var rc = await TestRunner.RunAsync(new TestCliOptions
        {
            CollectionPath = coll,
            ReportPath = report,
            JUnitPath = junit,
        });

        Assert.Equal(1, rc);
        Assert.False(File.Exists(report));
    }
}
