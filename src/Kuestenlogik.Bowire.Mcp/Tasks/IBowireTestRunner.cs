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
/// <param name="Test">#811: run only the collection's test of this name (a re-run from the app); null runs them all.</param>
public sealed record BowireTestRunRequest(string Path, string? DataPath, bool FailFast, string? JUnitPath, string? ReportPath, string? Test = null);

/// <summary>What a run did.</summary>
/// <param name="ExitCode"><c>bowire test</c>'s exit code: 0 passed, 1 a test failed, 2 the input was refused.</param>
/// <param name="Output">The run's console output: one line per test and assertion, iterations and rows included.</param>
/// <param name="Errors">What the run wrote as errors, e.g. why a data file was refused.</param>
/// <param name="Report">#811: the run step by step, with secrets masked; null when the input was refused before anything ran.</param>
public sealed record BowireTestRunOutcome(int ExitCode, string Output, string Errors, BowireTestRunReport? Report = null);

/// <summary>#811 — a run, step by step: what the test-run app shows.</summary>
/// <param name="Kind"><c>collection</c> or <c>flow</c>.</param>
/// <param name="Name">The collection's or the flow's name.</param>
/// <param name="DurationMs">How long the run took.</param>
/// <param name="Iterations">Data-set iterations run; 1 without a data set.</param>
/// <param name="Steps">The tests (of a collection) or the steps (of a flow), in the order they ran.</param>
public sealed record BowireTestRunReport(string Kind, string Name, long DurationMs, int Iterations, IReadOnlyList<BowireTestStepOutcome> Steps);

/// <summary>#811 — one test or flow step.</summary>
/// <param name="Name">The test's name, or the flow step's id.</param>
/// <param name="Service">The service it called.</param>
/// <param name="Method">The method it called.</param>
/// <param name="Status">The call's status, e.g. <c>OK</c>.</param>
/// <param name="DurationMs">How long the call took.</param>
/// <param name="Passed">True when the call succeeded and every check held.</param>
/// <param name="Skipped">True for a flow step the runner does not execute.</param>
/// <param name="Error">Why the call failed, if it did.</param>
/// <param name="Iteration">The data-set iteration; 0 without a data set.</param>
/// <param name="Source">Where the iteration's row came from, e.g. <c>rows.csv line 4</c>.</param>
/// <param name="Checks">The test's assertions, or the step's expectations.</param>
public sealed record BowireTestStepOutcome(
    string Name, string Service, string Method, string? Status, long DurationMs, bool Passed, bool Skipped,
    string? Error, int Iteration, string? Source, IReadOnlyList<BowireTestCheckOutcome> Checks);

/// <summary>#811 — one assertion or expectation.</summary>
/// <param name="Subject">What was checked: the assertion's path, or the expectation's id.</param>
/// <param name="Operator">How, e.g. <c>eq</c>.</param>
/// <param name="Expected">The expected value.</param>
/// <param name="Actual">The value found.</param>
/// <param name="Passed">Whether it held.</param>
/// <param name="Message">Why it did not, if it did not.</param>
public sealed record BowireTestCheckOutcome(string Subject, string Operator, string? Expected, string? Actual, bool Passed, string? Message);
