// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Schema;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire diff</c> — compare two API discovery snapshots (base → head) and
/// report which services and methods were added, removed, or signature-changed.
/// The schema half of the v2.5 PR bot (#183): a CI job captures a snapshot at
/// the base branch and another at the head, then diffs the two.
/// </summary>
/// <remarks>
/// <para>
/// The diff itself is the pure Core transform
/// <see cref="BowireSchemaDiff.Compute"/> — this file is the CLI plumbing:
/// resolve each side (a <c>.json</c> snapshot file, or a live URL to discover),
/// run the diff, render it as JSON or markdown, and translate the result into
/// an exit code the PR check can gate on.
/// </para>
/// <para>
/// The snapshot format is the same service-list JSON the workbench's
/// <c>GET /api/services</c> emits, so a snapshot captured either way is
/// interchangeable. <c>bowire diff snapshot &lt;url&gt;</c> captures one.
/// </para>
/// </remarks>
internal static class DiffCommand
{
    // JsonSerializerDefaults.Web = camelCase + case-insensitive, matching the
    // workbench's /api/services envelope so snapshots round-trip either way.
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions DeltaJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static Command Build()
    {
        var diff = new Command(
            "diff",
            "Diff two API schema snapshots (base -> head): services/methods added, removed, or signature-changed. The schema half of the PR bot.");

        var baseOpt = new Option<string?>("--base")
        {
            Description = "Base side: a .json snapshot file (from `bowire diff snapshot`) or a live URL to discover.",
        };
        var headOpt = new Option<string?>("--head")
        {
            Description = "Head side: a .json snapshot file or a live URL to discover.",
        };
        var formatOpt = new Option<string?>("--format", "-f")
        {
            Description = "Output format: 'json' (default) or 'markdown'.",
        };
        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Write the result to this file. When unset, it goes to stdout.",
        };
        var failOnOpt = new Option<string>("--fail-on")
        {
            Description = "Exit non-zero on: 'none' (default), 'breaking' (a removed service/method or signature change), or 'any' (any callable-surface change).",
            DefaultValueFactory = _ => "none",
        };
        var protocolOpt = new Option<string?>("--protocol")
        {
            Description = "Protocol plugin id for live-URL discovery (rest, grpc, graphql, websocket, mqtt, ...). Ignored for snapshot files. Guessed from the URL scheme when unset.",
        };

        diff.Add(baseOpt);
        diff.Add(headOpt);
        diff.Add(formatOpt);
        diff.Add(outputOpt);
        diff.Add(failOnOpt);
        diff.Add(protocolOpt);
        diff.SetAction(async (pr, ct) =>
            await RunDiffAsync(
                pr.GetValue(baseOpt),
                pr.GetValue(headOpt),
                pr.GetValue(formatOpt),
                pr.GetValue(outputOpt),
                pr.GetValue(failOnOpt) ?? "none",
                pr.GetValue(protocolOpt),
                ct,
                pr.InvocationConfiguration.Output,
                pr.InvocationConfiguration.Error).ConfigureAwait(false));

        diff.Add(BuildSnapshot(outputOpt, protocolOpt));
        return diff;
    }

    private static Command BuildSnapshot(Option<string?> outputOpt, Option<string?> protocolOpt)
    {
        var snapshot = new Command(
            "snapshot",
            "Discover a live URL and write the service list as a .json snapshot — the input `bowire diff` consumes.");

        var urlArg = new Argument<string>("url")
        {
            Description = "URL to discover (http(s)://, ws://, mqtt://, nats://, ...).",
        };

        snapshot.Add(urlArg);
        snapshot.Add(outputOpt);
        snapshot.Add(protocolOpt);
        snapshot.SetAction(async (pr, ct) =>
            await RunSnapshotAsync(
                pr.GetValue(urlArg) ?? "",
                pr.GetValue(outputOpt),
                pr.GetValue(protocolOpt),
                ct,
                pr.InvocationConfiguration.Output,
                pr.InvocationConfiguration.Error).ConfigureAwait(false));

        return snapshot;
    }

    // ---- actions --------------------------------------------------------

    internal static async Task<int> RunDiffAsync(
        string? baseSource, string? headSource, string? format, string? output,
        string failOn, string? protocolId,
        CancellationToken ct, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var outW = stdout ?? Console.Out;
        var errW = stderr ?? Console.Error;

        if (string.IsNullOrWhiteSpace(baseSource) || string.IsNullOrWhiteSpace(headSource))
        {
            await errW.WriteLineAsync(
                "Usage: bowire diff --base <snapshot|url> --head <snapshot|url> [--format json|markdown] [--fail-on none|breaking|any]").ConfigureAwait(false);
            return 2;
        }

        var before = await ResolveSnapshotAsync(baseSource, protocolId, errW, ct).ConfigureAwait(false);
        if (before is null) return 1;
        var after = await ResolveSnapshotAsync(headSource, protocolId, errW, ct).ConfigureAwait(false);
        if (after is null) return 1;

        var delta = BowireSchemaDiff.Compute(before, after);

        var rendered = string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase)
            ? delta.ToMarkdown()
            : JsonSerializer.Serialize(delta, DeltaJson);
        await WriteResultAsync(rendered, output, outW, ct).ConfigureAwait(false);

        return ExitCodeFor(delta, failOn);
    }

    internal static async Task<int> RunSnapshotAsync(
        string url, string? output, string? protocolId,
        CancellationToken ct, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var outW = stdout ?? Console.Out;
        var errW = stderr ?? Console.Error;

        if (string.IsNullOrWhiteSpace(url))
        {
            await errW.WriteLineAsync("Usage: bowire diff snapshot <url> [--protocol <id>] [-o <file>]").ConfigureAwait(false);
            return 2;
        }

        var services = await DiscoverAsync(url, protocolId, errW, ct).ConfigureAwait(false);
        if (services is null) return 1;

        var json = JsonSerializer.Serialize(services, SnapshotJson);
        await WriteResultAsync(json, output, outW, ct).ConfigureAwait(false);
        return 0;
    }

    // ---- helpers --------------------------------------------------------

    /// <summary>
    /// Translate a diff outcome into an exit code the PR check gates on:
    /// <c>breaking</c> fails on a removed surface or signature change,
    /// <c>any</c> fails on any callable-surface movement, <c>none</c> never fails.
    /// </summary>
    internal static int ExitCodeFor(BowireSchemaDelta delta, string failOn) => failOn switch
    {
        "breaking" => delta.HasBreakingChanges ? 1 : 0,
        "any" => delta.CallableMoved ? 1 : 0,
        _ => 0,
    };

    /// <summary>
    /// Resolve one side of the diff: a snapshot file if the path exists,
    /// otherwise a live URL to discover. Returns <c>null</c> (after writing a
    /// message) on failure.
    /// </summary>
    private static async Task<List<BowireServiceInfo>?> ResolveSnapshotAsync(
        string source, string? protocolId, TextWriter errW, CancellationToken ct)
    {
        if (File.Exists(source))
        {
            try
            {
                var json = await File.ReadAllTextAsync(source, ct).ConfigureAwait(false);
                var list = JsonSerializer.Deserialize<List<BowireServiceInfo>>(json, SnapshotJson);
                if (list is null)
                {
                    await errW.WriteLineAsync($"Snapshot '{source}' did not parse into a service list.").ConfigureAwait(false);
                }

                return list;
            }
            catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException or UnauthorizedAccessException)
            {
                await errW.WriteLineAsync($"Failed to read snapshot '{source}': {ex.Message}").ConfigureAwait(false);
                return null;
            }
        }

        return await DiscoverAsync(source, protocolId, errW, ct).ConfigureAwait(false);
    }

    private static async Task<List<BowireServiceInfo>?> DiscoverAsync(
        string url, string? protocolId, TextWriter errW, CancellationToken ct)
    {
        var id = string.IsNullOrWhiteSpace(protocolId) ? PickProtocolId(url) : protocolId;
        var protocol = ResolveProtocol(id);
        if (protocol is null)
        {
            await errW.WriteLineAsync(
                $"Protocol plugin '{id}' is not loaded. Pass --protocol with an installed plugin id.").ConfigureAwait(false);
            return null;
        }

        // Plugin DiscoverAsync is a 3rd-party transport surface: any failure
        // there is a discovery failure, not a crash. Filtered so cancellation
        // and OOM still propagate (the no-pragma boundary-catch convention).
        try
        {
            return await protocol.DiscoverAsync(url, showInternalServices: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            await errW.WriteLineAsync($"Discovery failed for {url}: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Best-effort protocol pick from a URL scheme; <c>--protocol</c> overrides.
    /// Defaults to REST for http(s) since that is the common case — a gRPC or
    /// GraphQL target over http should pass <c>--protocol</c> explicitly.
    /// </summary>
    private static string PickProtocolId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "rest";
        var scheme = uri.Scheme;
        if (Eq(scheme, "ws") || Eq(scheme, "wss")) return "websocket";
        if (Eq(scheme, "mqtt") || Eq(scheme, "mqtts")) return "mqtt";
        if (Eq(scheme, "nats")) return "nats";
        if (Eq(scheme, "kafka")) return "kafka";
        if (Eq(scheme, "amqp") || Eq(scheme, "amqps")) return "amqp";
        return "rest";

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static IBowireProtocol? ResolveProtocol(string id)
    {
        var registry = BowireProtocolRegistry.Discover();
        return registry.Protocols.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

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
