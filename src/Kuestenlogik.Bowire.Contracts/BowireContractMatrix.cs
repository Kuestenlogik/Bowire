// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Contracts;

/// <summary>
/// Cell verdict in the contract matrix. <see cref="NotRun"/> marks a
/// consumer × provider pair that has no verification result — the grid is
/// the cross-product of every consumer and provider seen, so pairs no one
/// verified show up as blanks rather than silently vanishing.
/// </summary>
public enum ContractCellStatus
{
    /// <summary>No verification report for this consumer × provider pair.</summary>
    NotRun,

    /// <summary>The provider satisfied every interaction.</summary>
    Pass,

    /// <summary>At least one interaction failed or errored.</summary>
    Fail,
}

/// <summary>
/// One consumer × provider cell in the matrix (#364). Carries the roll-up
/// (status + counts + last run) for the grid and the full
/// <see cref="Report"/> for the per-interaction drill-in.
/// </summary>
public sealed class ContractMatrixCell
{
    /// <summary>Row — the consumer party.</summary>
    public required string Consumer { get; init; }

    /// <summary>Column — the provider party.</summary>
    public required string Provider { get; init; }

    /// <summary>Cell verdict.</summary>
    public ContractCellStatus Status { get; init; }

    /// <summary>When the underlying run started (UTC); null when <see cref="Status"/> is <see cref="ContractCellStatus.NotRun"/>.</summary>
    public DateTime? LastRun { get; init; }

    /// <summary>Interactions that passed.</summary>
    public int PassedInteractions { get; init; }

    /// <summary>Total interactions in the contract.</summary>
    public int TotalInteractions { get; init; }

    /// <summary>The full verification report backing this cell, for drill-in; null when not run.</summary>
    public ContractVerificationReport? Report { get; init; }
}

/// <summary>
/// The assembled consumer × provider matrix: the two axes plus the cell
/// for every pair. <see cref="Cells"/> is dense — one entry per
/// (consumer, provider) combination, in row-major order — so a renderer
/// can lay out the grid without probing for gaps.
/// </summary>
public sealed class ContractMatrix
{
    /// <summary>Row labels (consumers), sorted.</summary>
    public required IReadOnlyList<string> Consumers { get; init; }

    /// <summary>Column labels (providers), sorted.</summary>
    public required IReadOnlyList<string> Providers { get; init; }

    /// <summary>Every cell, row-major (consumer-major) over the axes.</summary>
    public required IReadOnlyList<ContractMatrixCell> Cells { get; init; }

    /// <summary>Cells whose status is <see cref="ContractCellStatus.Fail"/>.</summary>
    public int FailedCells { get; init; }

    /// <summary>Cells whose status is <see cref="ContractCellStatus.Pass"/>.</summary>
    public int PassedCells { get; init; }
}

/// <summary>
/// Builds the consumer × provider result matrix (#364) from a set of
/// contract-verification reports. Pure aggregation — no I/O — so the
/// endpoint, the CLI <c>contract matrix</c> command and the MCP tool all
/// project the same grid from whatever set of reports they gathered.
/// </summary>
public static class BowireContractMatrix
{
    /// <summary>
    /// Assemble a matrix from <paramref name="reports"/>. Rows and columns
    /// are the distinct consumer and provider names across all reports.
    /// When more than one report exists for the same pair the most recent
    /// (by <see cref="ContractVerificationReport.StartedAt"/>) wins, so a
    /// re-run supersedes an earlier verdict rather than duplicating it.
    /// </summary>
    public static ContractMatrix Build(IEnumerable<ContractVerificationReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        // Collapse to the latest report per (consumer, provider) pair.
        var latest = new Dictionary<(string Consumer, string Provider), ContractVerificationReport>();
        foreach (var report in reports)
        {
            var key = (report.Consumer, report.Provider);
            if (!latest.TryGetValue(key, out var existing) || report.StartedAt >= existing.StartedAt)
            {
                latest[key] = report;
            }
        }

        var consumers = latest.Keys.Select(k => k.Consumer)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var providers = latest.Keys.Select(k => k.Provider)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cells = new List<ContractMatrixCell>(consumers.Count * providers.Count);
        var passed = 0;
        var failed = 0;
        foreach (var consumer in consumers)
        {
            foreach (var provider in providers)
            {
                if (latest.TryGetValue((consumer, provider), out var report))
                {
                    var status = report.Passed ? ContractCellStatus.Pass : ContractCellStatus.Fail;
                    if (status == ContractCellStatus.Pass) passed++; else failed++;
                    cells.Add(new ContractMatrixCell
                    {
                        Consumer = consumer,
                        Provider = provider,
                        Status = status,
                        LastRun = report.StartedAt,
                        PassedInteractions = report.Interactions.Count(i => i.Passed),
                        TotalInteractions = report.Interactions.Count,
                        Report = report,
                    });
                }
                else
                {
                    cells.Add(new ContractMatrixCell
                    {
                        Consumer = consumer,
                        Provider = provider,
                        Status = ContractCellStatus.NotRun,
                    });
                }
            }
        }

        return new ContractMatrix
        {
            Consumers = consumers,
            Providers = providers,
            Cells = cells,
            PassedCells = passed,
            FailedCells = failed,
        };
    }
}
