// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Contracts;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Projects the contract engine's <see cref="ContractVerificationReport"/>
/// onto the CLI's generic <see cref="RunReport"/> (#364).
/// <para>
/// The engine moved into Kuestenlogik.Bowire.Contracts so the workbench
/// endpoint and MCP share it, and it grew a report type of its own —
/// structured consumer / provider plus a timestamp, which the matrix needs
/// and a "C → P" title string could not carry. The CLI still emits JUnit /
/// SARIF / exit codes through the same emitters as <c>bowire test</c>, so
/// this adapter is the single seam where the two shapes meet rather than
/// having the emitters learn a second report type.
/// </para>
/// </summary>
internal static class ContractReportAdapter
{
    /// <summary>
    /// Map a verification report onto a <see cref="RunReport"/>, restoring
    /// the "consumer → provider" collection title the emitters print.
    /// </summary>
    public static RunReport ToRunReport(ContractVerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var run = new RunReport
        {
            CollectionName = $"{report.Consumer} → {report.Provider}",
            CollectionPath = string.Empty,
            StartedAt = report.StartedAt,
            DurationMs = report.DurationMs,
            TotalAssertions = report.TotalAssertions,
            PassedAssertions = report.PassedAssertions,
            FailedTests = report.FailedInteractions,
        };

        foreach (var interaction in report.Interactions)
        {
            var test = new TestResult
            {
                Name = interaction.Description,
                Service = report.Provider,
                Method = interaction.Method,
                DurationMs = interaction.DurationMs,
                Status = interaction.Status,
                Response = interaction.Response,
                Error = interaction.Error,
            };
            foreach (var assertion in interaction.Assertions)
            {
                test.Assertions.Add(new AssertionResult
                {
                    Path = assertion.Path,
                    Op = assertion.Op,
                    // RunReport's assertion fields are non-nullable; the
                    // engine leaves them null when there was nothing to
                    // compare (a transport error before any response).
                    Expected = assertion.Expected ?? string.Empty,
                    ActualText = assertion.ActualText ?? string.Empty,
                    Passed = assertion.Passed,
                    Error = assertion.Error,
                });
            }
            run.Tests.Add(test);
        }

        return run;
    }
}
