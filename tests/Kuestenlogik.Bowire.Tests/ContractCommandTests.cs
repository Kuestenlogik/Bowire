// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Contracts;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// <c>bowire contract</c> — the matrix a reviewer reads, and the file names it
/// derives from party names.
/// </summary>
public sealed class ContractCommandTests
{
    private static ContractMatrix Matrix(
        IReadOnlyList<string> consumers, IReadOnlyList<string> providers, params ContractMatrixCell[] cells)
        => new()
        {
            Consumers = consumers,
            Providers = providers,
            Cells = cells,
            PassedCells = cells.Count(c => c.Status == ContractCellStatus.Pass),
            FailedCells = cells.Count(c => c.Status == ContractCellStatus.Fail),
        };

    private static ContractMatrixCell Cell(
        string consumer, string provider, ContractCellStatus status, int passed = 0, int total = 0)
        => new()
        {
            Consumer = consumer,
            Provider = provider,
            Status = status,
            PassedInteractions = passed,
            TotalInteractions = total,
        };

    private static async Task<string> Render(ContractMatrix matrix)
    {
        using var writer = new StringWriter();
        await ContractCommand.PrintMatrixAsync(writer, matrix);
        return writer.ToString();
    }

    // ---- the grid ----

    [Fact]
    public async Task A_Cell_Shows_Its_Verdict_And_How_Many_Interactions_Backed_It()
    {
        // "PASS" alone hides whether one interaction was checked or forty.
        var text = await Render(Matrix(
            ["orders-web"], ["orders-api"],
            Cell("orders-web", "orders-api", ContractCellStatus.Pass, passed: 12, total: 12)));

        Assert.Contains("PASS 12/12", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Failing_Cell_Shows_How_Many_Held_Rather_Than_Just_Failing()
    {
        // 11 of 12 and 0 of 12 are very different mornings.
        var text = await Render(Matrix(
            ["orders-web"], ["orders-api"],
            Cell("orders-web", "orders-api", ContractCellStatus.Fail, passed: 11, total: 12)));

        Assert.Contains("FAIL 11/12", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Pair_That_Was_Never_Run_Reads_As_A_Dash_Not_As_A_Pass()
    {
        // The dangerous rendering would be a blank or a zero — both read as
        // "checked, nothing wrong" when nothing was checked at all.
        var text = await Render(Matrix(["orders-web"], ["orders-api"]));

        Assert.Contains("—", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_Consumer_Gets_A_Row_And_Every_Provider_A_Column()
    {
        var text = await Render(Matrix(
            ["web", "mobile"], ["orders-api", "billing-api"],
            Cell("web", "orders-api", ContractCellStatus.Pass, 3, 3),
            Cell("mobile", "billing-api", ContractCellStatus.Fail, 1, 4)));

        foreach (var name in new[] { "web", "mobile", "orders-api", "billing-api" })
            Assert.Contains(name, text, StringComparison.Ordinal);

        Assert.Contains("PASS 3/3", text, StringComparison.Ordinal);
        Assert.Contains("FAIL 1/4", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Grid_Lines_Up_When_Names_Differ_Wildly_In_Length()
    {
        // Column width follows the widest name; a fixed width would wrap the
        // grid into unreadability for one long service name.
        var text = await Render(Matrix(
            ["a-very-long-consumer-name-indeed", "x"], ["p"],
            Cell("a-very-long-consumer-name-indeed", "p", ContractCellStatus.Pass, 1, 1),
            Cell("x", "p", ContractCellStatus.Pass, 1, 1)));

        var rows = text.Split('\n')
            .Where(l => l.Contains("PASS", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, rows.Count);
        // Both verdicts start at the same column.
        Assert.Equal(
            rows[0].IndexOf("PASS", StringComparison.Ordinal),
            rows[1].IndexOf("PASS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_Summary_Counts_Passing_And_Failing_Pairs()
    {
        var text = await Render(Matrix(
            ["web"], ["a", "b"],
            Cell("web", "a", ContractCellStatus.Pass, 2, 2),
            Cell("web", "b", ContractCellStatus.Fail, 0, 3)));

        Assert.Contains("1 passing", text, StringComparison.Ordinal);
        Assert.Contains("1 failing", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Empty_Matrix_Renders_Without_Throwing()
    {
        // `bowire contract matrix` in a repo with no contracts yet — the
        // width calculations must not divide by an empty set.
        var text = await Render(Matrix([], []));

        Assert.Contains("0 passing", text, StringComparison.Ordinal);
    }

    // ---- report file names ----

    [Theory]
    [InlineData("orders-api", "orders-api")]
    [InlineData("Orders.Api", "Orders-Api")]
    [InlineData("orders api", "orders-api")]
    public void An_Ordinary_Name_Survives_As_A_File_Name(string input, string expected)
        => Assert.Equal(expected, ContractCommand.Sanitize(input));

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows")]
    [InlineData("/absolute/path")]
    [InlineData("C:\\Windows\\System32")]
    public void A_Name_That_Could_Traverse_Cannot(string input)
    {
        // Party names come out of contract files, which the person running
        // the command did not necessarily write. This decides what a report
        // path may contain.
        var safe = ContractCommand.Sanitize(input);

        Assert.DoesNotContain("..", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("/", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", safe, StringComparison.Ordinal);
        Assert.DoesNotContain(":", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void Leading_And_Trailing_Separators_Are_Trimmed()
        // "---orders---" would be a legal but silly file name.
        => Assert.Equal("orders", ContractCommand.Sanitize("...orders..."));

    [Fact]
    public void A_Name_Of_Nothing_Usable_Reduces_To_Empty_Rather_Than_To_Dashes()
        => Assert.Equal("", ContractCommand.Sanitize("///"));

    // ---- command surface ----

    [Fact]
    public void The_Command_Exposes_Its_Three_Verbs()
    {
        var names = ContractCommand.Build().Subcommands.Select(c => c.Name).ToList();

        Assert.Contains("matrix", names);
        Assert.Contains("publish", names);
        Assert.Contains("verify", names);
    }
}
