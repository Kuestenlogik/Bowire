// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Mcp.Tasks;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// The <c>bowire.test.run</c> MCP tool's runner (#802): the same
/// <see cref="TestRunner"/> <c>bowire test</c> uses, with its console output
/// captured for the agent, its progress forwarded as task status and its
/// cancellation honoured before each step.
/// </summary>
internal sealed class McpTestRunner : IBowireTestRunner
{
    public async Task<BowireTestRunOutcome> RunAsync(BowireTestRunRequest request, Func<string, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var options = new TestCliOptions
        {
            CollectionPath = Path.GetFullPath(request.Path),
            DataPath = string.IsNullOrWhiteSpace(request.DataPath) ? null : Path.GetFullPath(request.DataPath),
            FailFast = request.FailFast,
            JUnitPath = request.JUnitPath,
            ReportPath = request.ReportPath,
            Progress = progress,
        };
        var code = await TestRunner.RunAsync(options, output, errors, cancellationToken).ConfigureAwait(false);
        return new BowireTestRunOutcome(code, output.ToString().Trim(), errors.ToString().Trim());
    }
}
