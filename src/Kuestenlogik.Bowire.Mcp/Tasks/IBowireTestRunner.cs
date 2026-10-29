// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mcp.Tasks;

/// <summary>
/// Runs what <c>bowire test</c> runs — a test collection or a flow — for the
/// <c>bowire.test.run</c> MCP tool (#802). The runners live in the tool
/// (<c>bowire</c>), above this package, so <c>bowire mcp serve</c> registers
/// the implementation; a host without one answers the tool with a reason.
/// </summary>
public interface IBowireTestRunner
{
    /// <summary>
    /// Run <paramref name="request"/>. <paramref name="progress"/> receives a
    /// short line per step (<c>step 3/12 · Get user</c>). Cancellation is
    /// cooperative: the run stops before its next step and throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<BowireTestRunOutcome> RunAsync(BowireTestRunRequest request, Func<string, Task> progress, CancellationToken cancellationToken);
}

/// <summary>One <c>bowire.test.run</c> call.</summary>
/// <param name="Path">A test collection or flow file.</param>
/// <param name="DataPath">An optional CSV or JSON data set; one iteration per row (#675, #945).</param>
/// <param name="FailFast">With a data set: stop after the first failing iteration.</param>
/// <param name="JUnitPath">An optional JUnit XML report to write.</param>
/// <param name="ReportPath">An optional HTML report to write.</param>
public sealed record BowireTestRunRequest(string Path, string? DataPath, bool FailFast, string? JUnitPath, string? ReportPath);

/// <summary>What a run did.</summary>
/// <param name="ExitCode"><c>bowire test</c>'s exit code: 0 passed, 1 a test failed, 2 the input was refused.</param>
/// <param name="Output">The run's console output: one line per test and assertion, iterations and rows included.</param>
/// <param name="Errors">What the run wrote as errors, e.g. why a data file was refused.</param>
public sealed record BowireTestRunOutcome(int ExitCode, string Output, string Errors);
