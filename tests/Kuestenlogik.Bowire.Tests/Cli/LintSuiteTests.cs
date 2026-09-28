// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using System.Xml.Linq;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Models;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// <c>bowire test --suite=lint</c> (#583) — the design-time rules through the
/// test runner's report sinks.
/// </summary>
/// <remarks>
/// What is worth pinning is what a pipeline consumes: the exit code under each
/// gate, a JUnit file whose test cases are the rules, and a SARIF file Code
/// Scanning accepts. The findings themselves are the linter's and have their
/// own tests.
/// </remarks>
public sealed class LintSuiteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bowire-lintsuite-" + Guid.NewGuid().ToString("N"));

    public LintSuiteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// One service, one method, two findings of different weight: a password
    /// in the response (high) and a timestamp typed as a string (low).
    /// </summary>
    private string Snapshot(bool withSecret = true)
    {
        var fields = new List<BowireFieldInfo>
        {
            new("created_at", 1, "string", "optional", false, false, null, null),
        };
        if (withSecret) fields.Add(new("password", 2, "string", "optional", false, false, null, null));
        var service = new BowireServiceInfo("orders.v1.OrderService", "orders.v1",
        [
            new BowireMethodInfo("GetOrder", "orders.v1.OrderService/GetOrder", false, false,
                new BowireMessageInfo("GetOrderRequest", "GetOrderRequest", []),
                new BowireMessageInfo("Order", "Order", fields),
                "Unary"),
        ]);
        var path = Path.Combine(_dir, "surface.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new List<BowireServiceInfo> { service }, CliSchemaSnapshot.Json));
        return path;
    }

    private static async Task<(int Rc, string Stdout, string Stderr)> Test(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var cmd = BowireCli.BuildTestCommand(new ConfigurationBuilder().AddInMemoryCollection().Build());
        var rc = await cmd.Parse(args).InvokeAsync(new InvocationConfiguration
        {
            Output = stdout,
            Error = stderr,
        }, TestContext.Current.CancellationToken);
        return (rc, stdout.ToString(), stderr.ToString());
    }

    // ---- the gate ----

    [Fact]
    public async Task By_Default_It_Reports_And_Passes_As_Bowire_Lint_Does()
    {
        // Lint's default, not test's: adopting lint must not break a pipeline on
        // the first run, and the Info-level naming rules find something in
        // nearly every real API. Gating is something you write down.
        var (rc, stdout, _) = await Test("--suite", "lint", Snapshot());
        Assert.Equal(0, rc);
        Assert.Contains("password", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Any_Written_Down_Fails_On_Any_Finding()
    {
        var (rc, _, _) = await Test("--suite", "lint", Snapshot(), "--fail-on", "any");
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task Never_Reports_And_Passes()
    {
        var (rc, stdout, _) = await Test("--suite", "lint", Snapshot(), "--fail-on", "never");
        Assert.Equal(0, rc);
        Assert.Contains("password", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "high", 1)]
    [InlineData(false, "high", 0)]   // only the low timestamp finding is left
    [InlineData(false, "low", 1)]
    public async Task A_Severity_Means_What_It_Means_In_Bowire_Lint(bool withSecret, string failOn, int expected)
    {
        var (rc, _, _) = await Test("--suite", "lint", Snapshot(withSecret), "--fail-on", failOn);
        Assert.Equal(expected, rc);
    }

    [Fact]
    public async Task A_Gate_It_Does_Not_Know_Is_Refused_Rather_Than_Read_As_Never()
    {
        // `bowire lint` reads a misspelt level as "never fail", on purpose — it
        // is advisory there. Under `bowire test` the run is a gate, and a typo
        // that quietly turns the step green is the failure mode #740 was about.
        // The parser refuses it before the run starts, and says what it wanted.
        var (rc, _, stderr) = await Test("--suite", "lint", Snapshot(), "--fail-on", "hihg");
        Assert.NotEqual(0, rc);
        Assert.Contains("hihg", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Severity_Without_The_Lint_Suite_Is_Refused_Not_Read_As_Any()
    {
        // A test run has no severities. Taking "high" as "any" would gate on
        // something the operator did not write.
        var (rc, _, stderr) = await Test(Snapshot(), "--fail-on", "high");
        Assert.Equal(2, rc);
        Assert.Contains("--suite lint", stderr, StringComparison.Ordinal);
    }

    // ---- refusals ----

    [Fact]
    public async Task A_Suite_It_Does_Not_Know_Is_Refused()
    {
        var (rc, _, stderr) = await Test("--suite", "fuzz", Snapshot());
        Assert.Equal(2, rc);
        Assert.Contains("fuzz", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lint_And_A_Workspace_Together_Are_Refused()
    {
        // A workspace is a set of flows, lint reads an API surface. Taking both
        // would run one and drop the other without a word.
        var (rc, _, stderr) = await Test("--suite", "lint", Snapshot(), "--workspace-id", "harbor");
        Assert.Equal(2, rc);
        Assert.Contains("workspace", stderr, StringComparison.Ordinal);
    }

    // ---- the reports ----

    [Fact]
    public async Task JUnit_Has_One_Case_Per_Rule_And_Only_Rules_At_The_Gate_Fail()
    {
        var junit = Path.Combine(_dir, "lint.xml");
        await Test("--suite", "lint", Snapshot(), "--fail-on", "high", "--junit", junit);

        var doc = XDocument.Load(junit);
        var cases = doc.Descendants("testcase").ToList();
        // Per rule, not per finding: the case count must not move with the API.
        Assert.Equal(cases.Count, int.Parse(doc.Descendants("testsuite").Single().Attribute("tests")!.Value,
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(cases.Count >= 5);

        var failing = cases.Where(c => c.Element("failure") is not null).ToList();
        var secret = Assert.Single(failing);
        Assert.Contains("SENSITIVE", secret.Attribute("name")!.Value, StringComparison.Ordinal);

        // The low timestamp finding does not fail, but it is not lost either.
        var timestamp = cases.Single(c => c.Attribute("name")!.Value.Contains("TIMESTAMP", StringComparison.Ordinal));
        Assert.Null(timestamp.Element("failure"));
        Assert.Contains("created_at", timestamp.Element("system-out")!.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sarif_Carries_Every_Finding_At_Its_Level_And_Where_It_Is_In_The_Api()
    {
        var sarif = Path.Combine(_dir, "lint.sarif");
        await Test("--suite", "lint", Snapshot(), "--fail-on", "high", "--sarif", sarif);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(sarif, TestContext.Current.CancellationToken));
        var run = doc.RootElement.GetProperty("runs")[0];
        Assert.Equal("bowire-lint", run.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString());

        var results = run.GetProperty("results").EnumerateArray().ToList();
        // Both findings — the gate decides the exit code, not what is reported.
        Assert.Equal(2, results.Count);
        var secret = results.Single(r => r.GetProperty("ruleId").GetString()!.Contains("SENSITIVE", StringComparison.Ordinal));
        Assert.Equal("error", secret.GetProperty("level").GetString());
        var timestamp = results.Single(r => r.GetProperty("ruleId").GetString()!.Contains("TIMESTAMP", StringComparison.Ordinal));
        Assert.Equal("note", timestamp.GetProperty("level").GetString());

        var location = secret.GetProperty("locations")[0];
        Assert.Equal("orders.v1.OrderService/GetOrder.password",
            location.GetProperty("logicalLocations")[0].GetProperty("fullyQualifiedName").GetString());
        // Never an absolute path — Code Scanning rejects those.
        Assert.False(Path.IsPathRooted(
            location.GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString()));
    }

    [Fact]
    public async Task Annotations_Make_Failing_Findings_Errors_And_The_Rest_Warnings()
    {
        var (_, stdout, _) = await Test("--suite", "lint", Snapshot(), "--fail-on", "high", "--annotations");
        Assert.Contains("::error title=BWR-LINT-SENSITIVE-RESPONSE::", stdout, StringComparison.Ordinal);
        Assert.Contains("::warning title=BWR-LINT-STRING-TIMESTAMP::", stdout, StringComparison.Ordinal);
    }
}
