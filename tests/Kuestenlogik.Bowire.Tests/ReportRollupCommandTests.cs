// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Tests.Plugins;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// <c>bowire report rollup</c> — the portfolio view a platform team reads.
/// </summary>
/// <remarks>
/// <para>
/// It re-runs nothing: every number comes from a report already on disk. That
/// makes the reading itself the risk — a lint file the reader does not
/// recognise contributes nothing and says nothing, so a service with problems
/// looks exactly like a service with none.
/// </para>
/// <para>
/// The other half is the gate. <c>--fail-on high</c> in a nightly job is a
/// promise that someone hears about a high finding; a typo'd severity that
/// degrades to "never fail" would keep that promise silently unkept, which is
/// why it is a usage error rather than a default.
/// </para>
/// </remarks>
public sealed class ReportRollupCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-rollup-" + Guid.NewGuid().ToString("N"));

    public ReportRollupCommandTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<(int Exit, string Out, string Err)> Cli(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await BowireCli.RunAsync(
            args, new ConfigurationBuilder().Build(),
            plugins: TestPluginLoaders.None(), stdout: stdout, stderr: stderr,
            cancellationToken: TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private Task<(int Exit, string Out, string Err)> Rollup(params string[] extra)
        => Cli([.. new[] { "report", "rollup", "--from", _root }, .. extra]);

    /// <summary>Write a lint report for <paramref name="service"/> where the reader will find it.</summary>
    private string LintReport(string service, int high = 0, int medium = 0, int low = 0)
    {
        // The service name comes from the directory the file sits in — the
        // layout a CI job produces when it collects artefacts per service.
        var dir = Path.Combine(_root, service);
        Directory.CreateDirectory(dir);
        var findings = new List<object>();
        for (var i = 0; i < high; i++) findings.Add(new { severity = "High", ruleId = "R1", message = "m" });
        for (var i = 0; i < medium; i++) findings.Add(new { severity = "Medium", ruleId = "R2", message = "m" });
        for (var i = 0; i < low; i++) findings.Add(new { severity = "Low", ruleId = "R3", message = "m" });

        var path = Path.Combine(dir, "lint.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            findings,
            summary = new { total = findings.Count, high, medium, low, info = 0 },
        }));
        return path;
    }

    // ---- arguments ----

    [Fact]
    public async Task Rolling_Up_Without_A_Path_Is_A_Usage_Error()
    {
        var (exit, _, _) = await Cli("report", "rollup");

        Assert.NotEqual(0, exit);
    }

    [Fact]
    public async Task A_Severity_The_Gate_Does_Not_Know_Is_Refused_Rather_Than_Ignored()
    {
        // The failure this prevents: a nightly job with `--fail-on hgih` that
        // passes forever and nobody notices, because "never fail" and "nothing
        // to report" look the same in a green build.
        var (exit, _, err) = await Rollup("--fail-on", "hgih");

        Assert.Equal(64, exit);
        Assert.Contains("bad --fail-on", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Gate_Defaults_To_Never_Failing()
    {
        // Reporting-only is the mode a team adopts first.
        LintReport("orders-api", high: 3);

        var (exit, _, _) = await Rollup();

        Assert.Equal(0, exit);
    }

    // ---- reading ----

    [Fact]
    public async Task A_Path_With_No_Reports_Says_So_Instead_Of_Printing_An_Empty_Table()
    {
        var (exit, output, _) = await Rollup();

        Assert.Equal(0, exit);
        Assert.Contains("No Bowire reports found", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Lint_Report_Becomes_A_Row_Named_After_Its_Directory()
    {
        LintReport("orders-api", high: 1, medium: 2);

        var (exit, output, _) = await Rollup();

        Assert.Equal(0, exit);
        Assert.Contains("orders-api", output, StringComparison.Ordinal);
        Assert.Contains("HIGH", output, StringComparison.Ordinal);
        // The lint cell is high/medium/low, counted from the findings
        // themselves rather than from the summary the file happens to carry.
        Assert.Contains("1/2/0", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Several_Services_Are_Counted_In_The_Footer()
    {
        LintReport("orders-api", high: 1);
        LintReport("billing-api");

        var (_, output, _) = await Rollup();

        Assert.Contains("2 service(s)", output, StringComparison.Ordinal);
        Assert.Contains("1 at high", output, StringComparison.Ordinal);
        Assert.Contains("1 clean", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_File_The_Reader_Does_Not_Recognise_Is_Counted_As_Skipped()
    {
        // Silently ignoring it would be the same failure the whole tool exists
        // to prevent: a report that contributed nothing and said nothing.
        await File.WriteAllTextAsync(
            Path.Combine(_root, "notes.json"), """{"something":"else"}""",
            TestContext.Current.CancellationToken);

        var (_, output, _) = await Rollup();

        Assert.Contains("not recognised", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Explicit_Service_Name_Overrides_The_Directory()
    {
        // For a repo that produces one service's reports in several folders.
        LintReport("some-folder", high: 1);

        var (_, output, _) = await Rollup("--service", "checkout");

        Assert.Contains("checkout", output, StringComparison.Ordinal);
        Assert.DoesNotContain("some-folder", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Json_Form_Is_What_A_Dashboard_Reads()
    {
        LintReport("orders-api", high: 1);

        var (exit, output, _) = await Rollup("--json");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        var services = doc.RootElement.GetProperty("services").EnumerateArray().ToList();
        Assert.Equal("orders-api", Assert.Single(services).GetProperty("service").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("summary").GetProperty("atHigh").GetInt32());
    }

    // ---- the gate ----

    [Fact]
    public async Task A_High_Finding_Fails_A_Job_Gated_On_High()
    {
        LintReport("orders-api", high: 1);

        var (exit, _, err) = await Rollup("--fail-on", "high");

        Assert.Equal(1, exit);
        Assert.Contains("1 service(s) at or above high", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Medium_Finding_Does_Not_Fail_A_Job_Gated_On_High()
    {
        LintReport("orders-api", medium: 5);

        var (exit, _, _) = await Rollup("--fail-on", "high");

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task A_Medium_Finding_Does_Fail_A_Job_Gated_On_Medium()
    {
        LintReport("orders-api", medium: 1);

        var (exit, _, _) = await Rollup("--fail-on", "medium");

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task A_Clean_Portfolio_Passes_Even_The_Strictest_Gate()
    {
        LintReport("orders-api");

        var (exit, _, _) = await Rollup("--fail-on", "info");

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task The_Report_Is_Still_Printed_When_The_Gate_Fails()
    {
        // A CI job that fails without showing what failed sends everyone to
        // re-run it locally.
        LintReport("orders-api", high: 1);

        var (exit, output, _) = await Rollup("--fail-on", "high");

        Assert.Equal(1, exit);
        Assert.Contains("orders-api", output, StringComparison.Ordinal);
    }
}
