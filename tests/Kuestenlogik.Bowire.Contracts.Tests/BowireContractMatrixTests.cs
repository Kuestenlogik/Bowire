// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Contracts;

namespace Kuestenlogik.Bowire.Contracts.Tests;

/// <summary>
/// #364 — the consumer × provider matrix aggregation. Pure projection over
/// verification reports: axes, dense cells, latest-wins collapsing, and the
/// pass/fail rollup the workbench grid and `bowire contract matrix` render.
/// </summary>
public sealed class BowireContractMatrixTests
{
    [Fact]
    public void Build_NoReports_YieldsEmptyAxes()
    {
        var matrix = BowireContractMatrix.Build([]);

        Assert.Empty(matrix.Consumers);
        Assert.Empty(matrix.Providers);
        Assert.Empty(matrix.Cells);
        Assert.Equal(0, matrix.PassedCells);
        Assert.Equal(0, matrix.FailedCells);
    }

    [Fact]
    public void Build_SingleReport_ProducesOnePassingCell()
    {
        var matrix = BowireContractMatrix.Build([Report("web", "orders", passed: true)]);

        var cell = Assert.Single(matrix.Cells);
        Assert.Equal("web", cell.Consumer);
        Assert.Equal("orders", cell.Provider);
        Assert.Equal(ContractCellStatus.Pass, cell.Status);
        Assert.Equal(1, matrix.PassedCells);
        Assert.Equal(0, matrix.FailedCells);
        Assert.NotNull(cell.Report); // drill-in payload rides along
        Assert.NotNull(cell.LastRun);
    }

    [Fact]
    public void Build_FailingReport_MarksCellFailAndCountsIt()
    {
        var matrix = BowireContractMatrix.Build([Report("web", "orders", passed: false)]);

        var cell = Assert.Single(matrix.Cells);
        Assert.Equal(ContractCellStatus.Fail, cell.Status);
        Assert.Equal(0, cell.PassedInteractions);
        Assert.Equal(1, cell.TotalInteractions);
        Assert.Equal(1, matrix.FailedCells);
    }

    [Fact]
    public void Build_IsDense_UnverifiedPairsBecomeNotRunCells()
    {
        // Two consumers × two providers, but only the diagonal was run.
        // The grid must still be complete so a renderer can lay it out.
        var matrix = BowireContractMatrix.Build(
        [
            Report("web", "orders", passed: true),
            Report("mobile", "billing", passed: true),
        ]);

        Assert.Equal(["mobile", "web"], matrix.Consumers);      // sorted
        Assert.Equal(["billing", "orders"], matrix.Providers);  // sorted
        Assert.Equal(4, matrix.Cells.Count);                    // dense 2×2

        var notRun = matrix.Cells.Where(c => c.Status == ContractCellStatus.NotRun).ToList();
        Assert.Equal(2, notRun.Count);
        Assert.All(notRun, c =>
        {
            Assert.Null(c.Report);
            Assert.Null(c.LastRun);
        });
    }

    [Fact]
    public void Build_SamePairTwice_KeepsTheMostRecentRun()
    {
        // A re-run supersedes the earlier verdict rather than duplicating
        // the pair — the older failure must not linger in the grid.
        var older = Report("web", "orders", passed: false, startedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Report("web", "orders", passed: true, startedAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var matrix = BowireContractMatrix.Build([older, newer]);

        var cell = Assert.Single(matrix.Cells);
        Assert.Equal(ContractCellStatus.Pass, cell.Status);
        Assert.Equal(newer.StartedAt, cell.LastRun);
    }

    [Fact]
    public void Build_SamePairTwice_OrderIndependent()
    {
        // Same as above with the inputs reversed — latest-wins must not
        // depend on enumeration order.
        var older = Report("web", "orders", passed: false, startedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Report("web", "orders", passed: true, startedAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var matrix = BowireContractMatrix.Build([newer, older]);

        var cell = Assert.Single(matrix.Cells);
        Assert.Equal(ContractCellStatus.Pass, cell.Status);
        Assert.Equal(newer.StartedAt, cell.LastRun);
    }

    [Fact]
    public void Build_CellsAreRowMajorOverTheAxes()
    {
        var matrix = BowireContractMatrix.Build(
        [
            Report("web", "orders", passed: true),
            Report("mobile", "billing", passed: true),
        ]);

        // Row-major: all of consumer[0]'s cells, then consumer[1]'s.
        var expected = matrix.Consumers
            .SelectMany(c => matrix.Providers.Select(p => (c, p)))
            .ToList();
        var actual = matrix.Cells.Select(c => (c.Consumer, c.Provider)).ToList();
        Assert.Equal(expected, actual);
    }

    private static ContractVerificationReport Report(
        string consumer, string provider, bool passed, DateTime? startedAt = null)
    {
        var report = new ContractVerificationReport
        {
            Consumer = consumer,
            Provider = provider,
            StartedAt = startedAt ?? new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            TotalAssertions = 1,
            PassedAssertions = passed ? 1 : 0,
            FailedInteractions = passed ? 0 : 1,
        };
        var interaction = new ContractInteractionResult
        {
            Description = "GET /thing",
            Method = "GET",
            Status = passed ? "200" : "500",
        };
        interaction.Assertions.Add(new ContractAssertion
        {
            Path = "status",
            Op = "eq",
            Expected = "200",
            ActualText = passed ? "200" : "500",
            Passed = passed,
        });
        report.Interactions.Add(interaction);
        return report;
    }
}
