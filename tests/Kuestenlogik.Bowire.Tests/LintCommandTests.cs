// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Linting;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// <c>bowire lint</c> — its gate, its renderers and the ways it refuses.
/// </summary>
/// <remarks>
/// Two things here are contracts rather than implementation details. The exit
/// code is what a CI job branches on, and the rendered output is what a
/// developer reads in a build log or a PR comment. Both are consumed by people
/// and pipelines that never see this code.
/// </remarks>
public sealed class LintCommandTests
{
    private static BowireLintFinding Finding(
        BowireLintSeverity severity, string rule = "R001", string service = "orders.v1.OrderService",
        string? method = "GetOrder", string? field = null, string message = "something to look at")
        => new(rule, severity, service, method, field, message);

    // ---- the gate ----
    //
    // "--fail-on none" and a typo both mean "never fail", which is a
    // deliberate choice: a lint gate is advisory, and a misspelled level
    // should not break a build that was passing.

    [Theory]
    [InlineData("high", BowireLintSeverity.High, 1)]
    [InlineData("high", BowireLintSeverity.Medium, 0)]
    [InlineData("medium", BowireLintSeverity.High, 1)]
    [InlineData("medium", BowireLintSeverity.Medium, 1)]
    [InlineData("medium", BowireLintSeverity.Low, 0)]
    [InlineData("low", BowireLintSeverity.Low, 1)]
    [InlineData("info", BowireLintSeverity.Info, 1)]
    public void The_Gate_Fires_At_Its_Level_And_Anything_Worse(
        string failOn, BowireLintSeverity found, int expected)
        => Assert.Equal(expected, LintCommand.ExitCodeFor([Finding(found)], failOn));

    [Theory]
    [InlineData("HIGH")]
    [InlineData("High")]
    public void The_Gate_Level_Is_Case_Insensitive(string failOn)
        => Assert.Equal(1, LintCommand.ExitCodeFor([Finding(BowireLintSeverity.High)], failOn));

    [Theory]
    [InlineData("none")]
    [InlineData("nonsense")]
    [InlineData("")]
    public void An_Unrecognised_Level_Never_Fails_The_Build(string failOn)
        => Assert.Equal(0, LintCommand.ExitCodeFor([Finding(BowireLintSeverity.High)], failOn));

    [Fact]
    public void No_Findings_Passes_Whatever_The_Gate_Says()
        => Assert.Equal(0, LintCommand.ExitCodeFor([], "info"));

    // ---- text output ----

    [Fact]
    public void Text_Output_Puts_Severity_Rule_Location_And_Message_On_One_Line()
    {
        // One finding per line, because the first thing anyone does with this
        // in CI is grep it.
        var text = LintCommand.ToText([Finding(BowireLintSeverity.High, "R042", message: "unbounded stream")]);

        var line = text.Split('\n')[0];
        Assert.Contains("[HIGH]", line, StringComparison.Ordinal);
        Assert.Contains("R042", line, StringComparison.Ordinal);
        Assert.Contains("orders.v1.OrderService.GetOrder", line, StringComparison.Ordinal);
        Assert.Contains("unbounded stream", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Location_Names_Only_The_Parts_That_Exist()
    {
        // A service-level finding has no method to name, and appending an
        // empty segment would produce "orders.v1.OrderService." — which reads
        // as a truncation rather than as a whole-service finding.
        Assert.Contains("orders.v1.OrderService  ",
            LintCommand.ToText([Finding(BowireLintSeverity.Low, method: null)]), StringComparison.Ordinal);

        Assert.Contains("orders.v1.OrderService.GetOrder.customerId",
            LintCommand.ToText([Finding(BowireLintSeverity.Low, field: "customerId")]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_Clean_Run_Says_So_Rather_Than_Printing_Nothing()
    {
        // Empty output reads as "the tool did not run".
        Assert.Contains("no findings", LintCommand.ToText([]), StringComparison.Ordinal);
    }

    [Fact]
    public void The_Summary_Counts_Per_Severity_And_Gets_The_Plural_Right()
    {
        Assert.Contains("1 finding (1 high)",
            LintCommand.ToText([Finding(BowireLintSeverity.High)]), StringComparison.Ordinal);

        var mixed = LintCommand.ToText([
            Finding(BowireLintSeverity.High),
            Finding(BowireLintSeverity.Low),
            Finding(BowireLintSeverity.Low),
        ]);
        Assert.Contains("3 findings (1 high, 2 low)", mixed, StringComparison.Ordinal);
        // Severities nobody hit are left out rather than listed as zero.
        Assert.DoesNotContain("0 medium", mixed, StringComparison.Ordinal);
    }

    // ---- markdown output ----

    [Fact]
    public void Markdown_Groups_By_Severity_Worst_First()
    {
        // This lands in a PR comment, where the reader wants the worst thing
        // at the top and not to sort it themselves.
        var md = LintCommand.ToMarkdown([
            Finding(BowireLintSeverity.Low, "R-LOW"),
            Finding(BowireLintSeverity.High, "R-HIGH"),
            Finding(BowireLintSeverity.Medium, "R-MED"),
        ]);

        var high = md.IndexOf("**HIGH**", StringComparison.Ordinal);
        var medium = md.IndexOf("**MEDIUM**", StringComparison.Ordinal);
        var low = md.IndexOf("**LOW**", StringComparison.Ordinal);

        Assert.True(high >= 0 && medium > high && low > medium,
            $"expected HIGH < MEDIUM < LOW, got {high}/{medium}/{low}");
    }

    [Fact]
    public void Markdown_Omits_Severities_With_Nothing_In_Them()
    {
        var md = LintCommand.ToMarkdown([Finding(BowireLintSeverity.Info)]);

        Assert.Contains("**INFO**", md, StringComparison.Ordinal);
        Assert.DoesNotContain("**HIGH**", md, StringComparison.Ordinal);
        Assert.DoesNotContain("**MEDIUM**", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_Leads_With_The_Summary()
    {
        var md = LintCommand.ToMarkdown([Finding(BowireLintSeverity.High)]);
        Assert.StartsWith("**Design-time lint:** 1 finding (1 high).", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_Of_A_Clean_Run_Is_A_Single_Line()
    {
        var md = LintCommand.ToMarkdown([]);
        Assert.Contains("no findings", md, StringComparison.Ordinal);
        Assert.DoesNotContain("**HIGH**", md, StringComparison.Ordinal);
    }

    // ---- refusals ----

    [Fact]
    public async Task No_Source_Prints_The_Usage_Line_And_Exits_2()
    {
        // 2, not 1: "you typed it wrong" and "the lint gate fired" are
        // different outcomes and a CI job may want to tell them apart.
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var rc = await LintCommand.RunAsync("", null, null, "none", null, null,
            TestContext.Current.CancellationToken, stdout, stderr);

        Assert.Equal(2, rc);
        Assert.Contains("Usage: bowire lint", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Rules_File_That_Is_Not_There_Is_An_Error_Not_A_Silent_Default()
    {
        // Falling back to the discovered config would run a lint the operator
        // did not ask for and report it as though they had.
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var missing = Path.Combine(Path.GetTempPath(), "bowire-no-such-rules-" + Guid.NewGuid().ToString("N") + ".json");

        var rc = await LintCommand.RunAsync("http://example.invalid", null, null, "none", null, missing,
            TestContext.Current.CancellationToken, stdout, stderr);

        Assert.Equal(1, rc);
        Assert.Contains("rules file not found", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains(missing, stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Unreadable_Rules_File_Names_The_File_And_The_Reason()
    {
        var path = Path.Combine(Path.GetTempPath(), "bowire-bad-rules-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{ not json", TestContext.Current.CancellationToken);
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var rc = await LintCommand.RunAsync("http://example.invalid", null, null, "none", null, path,
                TestContext.Current.CancellationToken, stdout, stderr);

            Assert.Equal(1, rc);
            Assert.Contains("failed to read rules config", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains(path, stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_Command_Exposes_The_Flags_Its_Usage_Line_Promises()
    {
        // The usage string above is hand-written, so a flag rename would
        // otherwise leave it advertising something that no longer exists.
        var names = LintCommand.Build().Options.Select(o => o.Name).ToList();

        Assert.Contains("--format", names);
        Assert.Contains("--fail-on", names);
        Assert.Contains("--rules", names);
        Assert.Contains("--output", names);
    }
}
