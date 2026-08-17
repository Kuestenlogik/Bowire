// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Linting;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire lint</c> — run the design-time schema linter (#189) over a
/// discovered API surface and report the design smells it finds (secrets in
/// responses, unbounded lists, missing versioning, ...). Sibling of
/// <c>bowire diff</c>: same snapshot input (a <c>.json</c> file or a live URL,
/// resolved by <see cref="CliSchemaSnapshot"/>), a <c>--fail-on</c> gate for CI.
/// </summary>
internal static class LintCommand
{
    private static readonly JsonSerializerOptions FindingsJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Command Build()
    {
        var lint = new Command(
            "lint",
            "Run design-time rules over an API surface (secrets in responses, unbounded lists, missing versioning, ...). Reads a snapshot file or a live URL.");

        var sourceArg = new Argument<string>("source")
        {
            Description = "A .json snapshot file (from `bowire diff snapshot`) or a live URL to discover.",
        };
        var formatOpt = new Option<string?>("--format", "-f")
        {
            Description = "Output format: 'text' (default), 'json', or 'markdown'.",
        };
        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Write the result to this file. When unset, it goes to stdout.",
        };
        var failOnOpt = new Option<string>("--fail-on")
        {
            Description = "Exit non-zero when a finding is at or above this severity: 'none' (default), 'info', 'low', 'medium', or 'high'.",
            DefaultValueFactory = _ => "none",
        };
        var protocolOpt = new Option<string?>("--protocol")
        {
            Description = "Protocol plugin id for live-URL discovery (rest, grpc, graphql, ...). Ignored for snapshot files. Guessed from the URL scheme when unset.",
        };
        var rulesOpt = new Option<string?>("--rules")
        {
            Description = "Path to a .bowire/rules.json config (rule on/off + severity overrides). Auto-discovered by walking up from the current directory when unset.",
        };

        lint.Add(sourceArg);
        lint.Add(formatOpt);
        lint.Add(outputOpt);
        lint.Add(failOnOpt);
        lint.Add(protocolOpt);
        lint.Add(rulesOpt);
        lint.SetAction(async (pr, ct) =>
            await RunAsync(
                pr.GetValue(sourceArg) ?? "",
                pr.GetValue(formatOpt),
                pr.GetValue(outputOpt),
                pr.GetValue(failOnOpt) ?? "none",
                pr.GetValue(protocolOpt),
                pr.GetValue(rulesOpt),
                ct,
                pr.InvocationConfiguration.Output,
                pr.InvocationConfiguration.Error).ConfigureAwait(false));

        return lint;
    }

    internal static async Task<int> RunAsync(
        string source, string? format, string? output, string failOn, string? protocolId,
        string? rulesPath,
        CancellationToken ct, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var outW = stdout ?? Console.Out;
        var errW = stderr ?? Console.Error;

        if (string.IsNullOrWhiteSpace(source))
        {
            await errW.WriteLineAsync("Usage: bowire lint <snapshot|url> [--format text|json|markdown] [--fail-on none|info|low|medium|high] [--rules <file>]").ConfigureAwait(false);
            return 2;
        }

        var (config, configFailed) = await LoadConfigAsync(rulesPath, errW).ConfigureAwait(false);
        if (configFailed) return 1;

        var services = await CliSchemaSnapshot.ResolveAsync(source, protocolId, errW, ct).ConfigureAwait(false);
        if (services is null) return 1;

        var findings = BowireSchemaLinter.CreateWithDiscoveredRules().Lint(services, config);

        var rendered = format?.ToUpperInvariant() switch
        {
            "JSON" => JsonSerializer.Serialize(findings, FindingsJson),
            "MARKDOWN" => ToMarkdown(findings),
            _ => ToText(findings),
        };
        await WriteResultAsync(rendered, output, outW, ct).ConfigureAwait(false);

        return ExitCodeFor(findings, failOn);
    }

    /// <summary>
    /// Resolve the lint config: an explicit <paramref name="rulesPath"/> (error
    /// if it doesn't exist), else the auto-discovered <c>.bowire/rules.json</c>
    /// (silent when none is found). Returns the config (or null) and whether it
    /// failed to load.
    /// </summary>
    private static async Task<(BowireLintConfig? Config, bool Failed)> LoadConfigAsync(string? rulesPath, TextWriter errW)
    {
        string? path;
        if (!string.IsNullOrWhiteSpace(rulesPath))
        {
            if (!File.Exists(rulesPath))
            {
                await errW.WriteLineAsync($"bowire lint: rules file not found: '{rulesPath}'.").ConfigureAwait(false);
                return (null, true);
            }

            path = rulesPath;
        }
        else
        {
            path = BowireLintConfigLoader.DiscoverPath();
        }

        if (path is null) return (null, false);

        try
        {
            return (BowireLintConfigLoader.Load(path), false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            await errW.WriteLineAsync($"bowire lint: failed to read rules config '{path}': {ex.Message}").ConfigureAwait(false);
            return (null, true);
        }
    }

    // ---- gate -----------------------------------------------------------

    internal static int ExitCodeFor(IReadOnlyList<BowireLintFinding> findings, string failOn)
    {
        var threshold = ParseThreshold(failOn);
        if (threshold is { } t && findings.Any(f => f.Severity >= t)) return 1;
        return 0;
    }

    private static BowireLintSeverity? ParseThreshold(string value)
    {
        if (Eq(value, "info")) return BowireLintSeverity.Info;
        if (Eq(value, "low")) return BowireLintSeverity.Low;
        if (Eq(value, "medium")) return BowireLintSeverity.Medium;
        if (Eq(value, "high")) return BowireLintSeverity.High;
        return null;   // "none" or anything unrecognised never fails.

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // ---- renderers ------------------------------------------------------

    private static string ToText(IReadOnlyList<BowireLintFinding> findings)
    {
        var sb = new StringBuilder();
        foreach (var f in findings)
        {
            sb.Append('[').Append(Label(f.Severity)).Append("] ")
              .Append(f.RuleId).Append("  ")
              .Append(Location(f)).Append("  ")
              .Append(f.Message).Append('\n');
        }

        sb.Append('\n').Append(Summary(findings));
        return sb.ToString();
    }

    private static string ToMarkdown(IReadOnlyList<BowireLintFinding> findings)
    {
        var sb = new StringBuilder();
        sb.Append("**Design-time lint:** ").Append(Summary(findings)).Append(".\n");
        foreach (var severity in new[] { BowireLintSeverity.High, BowireLintSeverity.Medium, BowireLintSeverity.Low, BowireLintSeverity.Info })
        {
            var group = findings.Where(f => f.Severity == severity).ToList();
            if (group.Count == 0) continue;
            sb.Append("\n**").Append(Label(severity)).Append("**\n");
            foreach (var f in group)
                sb.Append("- `").Append(Location(f)).Append("` [").Append(f.RuleId).Append("] — ").Append(f.Message).Append('\n');
        }

        return sb.ToString();
    }

    private static string Summary(IReadOnlyList<BowireLintFinding> findings)
    {
        if (findings.Count == 0) return "no findings";
        var parts = new List<string>();
        foreach (var severity in new[] { BowireLintSeverity.High, BowireLintSeverity.Medium, BowireLintSeverity.Low, BowireLintSeverity.Info })
        {
            var n = findings.Count(f => f.Severity == severity);
            if (n > 0) parts.Add($"{n} {LabelLower(severity)}");
        }

        return $"{findings.Count} finding{(findings.Count == 1 ? "" : "s")} ({string.Join(", ", parts)})";
    }

    private static string Location(BowireLintFinding f)
    {
        var sb = new StringBuilder(f.Service);
        if (!string.IsNullOrEmpty(f.Method)) sb.Append('.').Append(f.Method);
        if (!string.IsNullOrEmpty(f.Field)) sb.Append('.').Append(f.Field);
        return sb.ToString();
    }

    private static string Label(BowireLintSeverity severity) => severity switch
    {
        BowireLintSeverity.High => "HIGH",
        BowireLintSeverity.Medium => "MEDIUM",
        BowireLintSeverity.Low => "LOW",
        _ => "INFO",
    };

    private static string LabelLower(BowireLintSeverity severity) => severity switch
    {
        BowireLintSeverity.High => "high",
        BowireLintSeverity.Medium => "medium",
        BowireLintSeverity.Low => "low",
        _ => "info",
    };

    private static async Task WriteResultAsync(string content, string? output, TextWriter stdout, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(output))
        {
            await stdout.WriteLineAsync(content).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(output, content, ct).ConfigureAwait(false);
            await stdout.WriteLineAsync($"  Wrote {output} ({content.Length:N0} chars).").ConfigureAwait(false);
        }
    }
}
