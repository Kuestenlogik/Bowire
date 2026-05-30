// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.AsyncApi;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Rest;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire export</c> — runs a wire-plugin discovery against a live
/// URL and emits the result as a schema artefact (OpenAPI 3.0 for
/// REST, AsyncAPI 3.0 for messaging). Optional <c>--recording &lt;file&gt;</c>
/// stamps each operation with an <c>x-bowire-coverage</c> extension
/// so consumers can tell which slice of the contract the recording
/// can actually replay vs. which slice would fall back to schema-
/// generated samples.
/// </summary>
/// <remarks>
/// <para>
/// The exporters themselves
/// (<see cref="OpenApiDocumentBuilder"/> /
/// <see cref="AsyncApiDocumentBuilder"/>) are pure transforms that
/// take a <see cref="BowireServiceInfo"/> list. This file is the
/// CLI plumbing: pick the right wire plugin via
/// <c>BowireProtocolRegistry.Discover()</c>, run its
/// discovery, optionally load a recording, then drive the builder
/// and write the result.
/// </para>
/// <para>
/// Sibling of <c>bowire mock --schema</c>: that command goes
/// schema → live mock endpoint; <c>bowire export</c> goes the other
/// way, live target → schema. Together they round-trip a captured
/// surface back into a portable artefact.
/// </para>
/// </remarks>
internal static class ExportCommand
{
    public static Command Build()
    {
        var export = new Command(
            "export",
            "Emit a portable schema (OpenAPI / AsyncAPI) from a live target's discovery result.");

        var urlArg = new Argument<string>("url")
        {
            Description = "URL to run discovery against (http(s)://, mqtt://, nats://, kafka://, ws://, amqp://, ...)."
        };
        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Write the document to this file. When unset, the document is written to stdout."
        };
        var formatOpt = new Option<string?>("--format", "-f")
        {
            Description = "Output format: 'yaml' (default) or 'json'."
        };
        var recordingOpt = new Option<string?>("--recording")
        {
            Description = "Path to a .bwr recording. Each operation is stamped with an x-bowire-coverage extension reporting whether the recording carries replay steps for it. When unset, no coverage block is emitted."
        };
        var titleOpt = new Option<string?>("--title")
        {
            Description = "Override the info.title field. Defaults to the host name."
        };
        var versionOpt = new Option<string?>("--version-info")
        {
            Description = "Override the info.version field. Defaults to '1.0.0' or the first service.Version found."
        };

        // ----- openapi subcommand --------------------------------
        var openapi = new Command("openapi",
            "Run REST discovery against <url> and emit an OpenAPI 3.0 document.");
        openapi.Add(urlArg);
        openapi.Add(outputOpt); openapi.Add(formatOpt); openapi.Add(recordingOpt);
        openapi.Add(titleOpt); openapi.Add(versionOpt);
        openapi.SetAction(async (pr, ct) =>
            await RunOpenApiAsync(
                pr.GetValue(urlArg) ?? "",
                pr.GetValue(outputOpt),
                pr.GetValue(formatOpt),
                pr.GetValue(recordingOpt),
                pr.GetValue(titleOpt),
                pr.GetValue(versionOpt),
                ct).ConfigureAwait(false));

        // ----- asyncapi subcommand --------------------------------
        var asyncapi = new Command("asyncapi",
            "Run messaging discovery against <url> and emit an AsyncAPI 3.0 document.");
        asyncapi.Add(urlArg);
        asyncapi.Add(outputOpt); asyncapi.Add(formatOpt); asyncapi.Add(recordingOpt);
        asyncapi.Add(titleOpt); asyncapi.Add(versionOpt);
        asyncapi.SetAction(async (pr, ct) =>
            await RunAsyncApiAsync(
                pr.GetValue(urlArg) ?? "",
                pr.GetValue(outputOpt),
                pr.GetValue(formatOpt),
                pr.GetValue(recordingOpt),
                pr.GetValue(titleOpt),
                pr.GetValue(versionOpt),
                ct).ConfigureAwait(false));

        export.Add(openapi);
        export.Add(asyncapi);
        return export;
    }

    // ---- subcommand implementations -------------------------------

    internal static async Task<int> RunOpenApiAsync(
        string url, string? output, string? format,
        string? recordingPath, string? title, string? versionOverride,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            await Console.Error.WriteLineAsync("Usage: bowire export openapi <url> [--output <file>] [--recording <file>]").ConfigureAwait(false);
            return 2;
        }

        // REST plugin sits in this assembly's classpath (csproj
        // ProjectReference), so it's always loadable; the discovery
        // call hits the live endpoint for the OpenAPI doc.
        var protocol = ResolveProtocol("rest");
        if (protocol is null)
        {
            await Console.Error.WriteLineAsync(
                "REST plugin not loaded. Install Kuestenlogik.Bowire.Protocol.Rest or use the bundled `bowire` tool.").ConfigureAwait(false);
            return 1;
        }

        List<BowireServiceInfo> services;
        try
        {
            services = await protocol.DiscoverAsync(url, showInternalServices: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Discovery failed for {url}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        var recording = LoadRecording(recordingPath);
        var options = BuildOpenApiOptions(format, title, versionOverride);
        var doc = OpenApiDocumentBuilder.Build(url, services, recording, options);
        await WriteResultAsync(doc, output, ct).ConfigureAwait(false);
        return 0;
    }

    internal static async Task<int> RunAsyncApiAsync(
        string url, string? output, string? format,
        string? recordingPath, string? title, string? versionOverride,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            await Console.Error.WriteLineAsync("Usage: bowire export asyncapi <url> [--output <file>] [--recording <file>]").ConfigureAwait(false);
            return 2;
        }

        // Pick the wire plugin by URL scheme — mqtt://, nats://,
        // kafka://, ws://, amqp(s):// (1.0 via amqp1://), pulsar://,
        // http(s):// for HTTP-bound AsyncAPI. The protocol-registry
        // lookup matches the resolver convention on the loader side.
        var protocolId = PickAsyncApiProtocolId(url);
        if (protocolId is null)
        {
            await Console.Error.WriteLineAsync(
                $"Can't tell which wire plugin to use for '{url}'. Expected scheme: mqtt, nats, kafka, ws, wss, amqp, amqp1, pulsar, http, https.").ConfigureAwait(false);
            return 2;
        }
        var protocol = ResolveProtocol(protocolId);
        if (protocol is null)
        {
            await Console.Error.WriteLineAsync(
                $"Wire plugin '{protocolId}' is not loaded. Install Kuestenlogik.Bowire.Protocol.{Capitalise(protocolId)} (or the matching package).").ConfigureAwait(false);
            return 1;
        }

        List<BowireServiceInfo> services;
        try
        {
            services = await protocol.DiscoverAsync(url, showInternalServices: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Discovery failed for {url}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        var recording = LoadRecording(recordingPath);
        var options = BuildAsyncApiOptions(format, title, versionOverride);
        var doc = AsyncApiDocumentBuilder.Build(url, services, recording, options);
        await WriteResultAsync(doc, output, ct).ConfigureAwait(false);
        return 0;
    }

    // ---- pure helpers (unit-testable) -----------------------------

    /// <summary>
    /// Map a URL scheme to the wire-plugin id that handles AsyncAPI's
    /// matching binding key. Returns <c>null</c> for schemes we have
    /// no binding for (TCP, UDP, file, &amp;c).
    /// </summary>
    internal static string? PickAsyncApiProtocolId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        // OpenAPI mandates lowercase keys, AsyncAPI ditto; CA1308
        // suppression matches the builder side.
#pragma warning disable CA1308
        var scheme = uri.Scheme.ToLowerInvariant();
#pragma warning restore CA1308
        return scheme switch
        {
            "mqtt" or "mqtts" => "mqtt",
            "nats" => "nats",
            "kafka" => "kafka",
            "ws" or "wss" => "websocket",
            "amqp" or "amqps" => "amqp",
            "amqp1" or "amqps1" => "amqp",   // same plugin, two URL schemes
            "pulsar" or "pulsar+ssl" => "pulsar",
            "http" or "https" => "rest",
            _ => null,
        };
    }

    /// <summary>
    /// Load a recording from <paramref name="path"/> — accepts either a
    /// single recording or a recording store (the on-disk shape the
    /// mock-server reads). Returns the first recording in a store, or
    /// the recording itself for a single-recording file.
    /// </summary>
    internal static BowireRecording? LoadRecording(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!File.Exists(path)) return null;

        try
        {
            var text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("recordings", out var arr)
                && arr.ValueKind == JsonValueKind.Array
                && arr.GetArrayLength() > 0)
            {
                // Store shape: take the first recording.
                var first = arr.EnumerateArray().First();
                return JsonSerializer.Deserialize<BowireRecording>(first.GetRawText());
            }
            // Bare-recording shape:
            return JsonSerializer.Deserialize<BowireRecording>(text);
        }
        catch (Exception)
        {
            // Bad recording file shouldn't kill the export — the
            // coverage block is informational, not required.
            return null;
        }
    }

    internal static OpenApiExportOptions BuildOpenApiOptions(string? format, string? title, string? version)
        => new()
        {
            Format = ParseOpenApiFormat(format),
            Title = title,
            Version = version,
        };

    internal static AsyncApiExportOptions BuildAsyncApiOptions(string? format, string? title, string? version)
        => new()
        {
            Format = ParseAsyncApiFormat(format),
            Title = title,
            Version = version,
        };

    internal static OpenApiExportFormat ParseOpenApiFormat(string? format)
        => string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
            ? OpenApiExportFormat.Json
            : OpenApiExportFormat.Yaml;

    internal static AsyncApiExportFormat ParseAsyncApiFormat(string? format)
        => string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
            ? AsyncApiExportFormat.Json
            : AsyncApiExportFormat.Yaml;

    private static async Task WriteResultAsync(string doc, string? output, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(output))
        {
            await Console.Out.WriteAsync(doc).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(output, doc, ct).ConfigureAwait(false);
            Console.WriteLine($"  Wrote {output} ({doc.Length:N0} chars).");
        }
    }

    private static IBowireProtocol? ResolveProtocol(string id)
    {
        var registry = BowireProtocolRegistry.Discover();
        return registry.Protocols.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static string Capitalise(string id)
        => string.IsNullOrEmpty(id) ? id : char.ToUpperInvariant(id[0]) + id[1..];
}
