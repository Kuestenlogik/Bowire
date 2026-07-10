// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Implementation of <c>bowire scan report</c> (#107): turns a scanner SARIF
/// artifact (from <c>bowire scan --out</c>) into a deterministic markdown report
/// — findings grouped by severity + OWASP, optionally diffed against a baseline
/// SARIF (new / fixed / still-open). No AI; the AI executive-summary layer is
/// the <c>POST /api/ai/security-report</c> endpoint.
/// </summary>
public static class ReportCommand
{
    /// <summary>Exit codes: 0 ok, 2 usage / file error.</summary>
    public static async Task<int> RunAsync(
        string sarifPath, string? baselinePath, string? outPath, string? target,
        CancellationToken ct, TextWriter? output = null, TextWriter? error = null)
    {
        var stdout = output ?? Console.Out;
        var stderr = error ?? Console.Error;

        if (string.IsNullOrWhiteSpace(sarifPath))
        {
            await stderr.WriteLineAsync("  Usage: bowire scan report --in <sarif.json> [--baseline <prev.json>] [--out <report.md>] [--target <name>]").ConfigureAwait(false);
            return 2;
        }
        if (!File.Exists(sarifPath))
        {
            await stderr.WriteLineAsync($"  SARIF file not found: {sarifPath}").ConfigureAwait(false);
            return 2;
        }

        string sarif;
        string? baseline = null;
        try
        {
            sarif = await File.ReadAllTextAsync(sarifPath, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(baselinePath))
            {
                if (!File.Exists(baselinePath))
                {
                    await stderr.WriteLineAsync($"  Baseline SARIF not found: {baselinePath}").ConfigureAwait(false);
                    return 2;
                }
                baseline = await File.ReadAllTextAsync(baselinePath, ct).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            await stderr.WriteLineAsync($"  Could not read SARIF: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        string markdown;
        try
        {
            markdown = SecurityReportBuilder.Build(sarif, baseline, target).ToMarkdown();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            await stderr.WriteLineAsync($"  Could not build report: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(outPath, markdown, ct).ConfigureAwait(false);
            await stdout.WriteLineAsync($"  Report written to {outPath}").ConfigureAwait(false);
        }
        else
        {
            await stdout.WriteLineAsync(markdown).ConfigureAwait(false);
        }
        return 0;
    }
}
