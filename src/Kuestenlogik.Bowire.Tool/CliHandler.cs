// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Handles CLI subcommands: list, describe, call.
/// Reuses GrpcReflectionClient and GrpcInvoker from the library.
/// </summary>
internal static class CliHandler
{
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    // Color heuristic: same source as the pre-refactor static property —
    // "no colour when stdout looks redirected" — but evaluated per writer
    // so a test-supplied StringWriter (which isn't a TTY) gets plain
    // text while the production Console.Out keeps its ANSI sequences.
    private static bool UseColor(TextWriter writer) =>
        ReferenceEquals(writer, Console.Out) && !Console.IsOutputRedirected;

    public static async Task<int> ListAsync(CliCommandOptions cli, TextWriter? stdout = null, TextWriter? stderr = null)
        => await RunWithErrorHandling(cli, CommandIo.Resolve(stdout, stderr), ListImplAsync).ConfigureAwait(false);
    public static async Task<int> DiscoverAsync(CliCommandOptions cli, TextWriter? stdout = null, TextWriter? stderr = null)
        => await RunWithErrorHandling(cli, CommandIo.Resolve(stdout, stderr), DiscoverImplAsync).ConfigureAwait(false);
    public static async Task<int> DescribeAsync(CliCommandOptions cli, TextWriter? stdout = null, TextWriter? stderr = null)
        => await RunWithErrorHandling(cli, CommandIo.Resolve(stdout, stderr), DescribeImplAsync).ConfigureAwait(false);

    /// <summary>
    /// <c>bowire call</c>. <paramref name="ct"/> is optional and last so
    /// every existing caller keeps compiling; it matters for
    /// <c>--stream</c>, where the command blocks until the server ends
    /// the stream and Ctrl+C is the only way out.
    /// </summary>
    public static async Task<int> CallAsync(CliCommandOptions cli, TextWriter? stdout = null,
        TextWriter? stderr = null, CancellationToken ct = default)
        => await RunWithErrorHandling(cli, CommandIo.Resolve(stdout, stderr),
            (c, io) => CallImplAsync(c, io, ct)).ConfigureAwait(false);

    private static async Task<int> RunWithErrorHandling(CliCommandOptions cli, CommandIo io,
        Func<CliCommandOptions, CommandIo, Task<int>> impl)
    {
        ArgumentNullException.ThrowIfNull(cli);
        // Top-level CLI error handler: anything thrown by an impl (gRPC
        // reflection, transcoding, JSON parse, plugin call) gets rendered
        // to stderr with exit 1. The catch-all is the point of the method;
        // CA1031 is switched off repo-wide in .editorconfig for exactly
        // this shape, so no pragma is needed (#538 removed the dead pair
        // that predated that setting).
        try
        {
            return await impl(cli, io).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteError(io, ex.Message);
            if (ex.InnerException is not null)
                WriteError(io, $"  {ex.InnerException.Message}");
            return 1;
        }
    }

    private static async Task<int> ListImplAsync(CliCommandOptions cli, CommandIo io)
    {
        using var client = new GrpcReflectionClient(cli.Url, showInternalServices: false);
        var services = await client.ListServicesAsync();

        if (services.Count == 0)
        {
            WriteWarning(io, "No gRPC services found. Is server reflection enabled?");
            return 0;
        }

        var color = UseColor(io.Out);
        foreach (var svc in services)
        {
            var methodCount = svc.Methods.Count;
            Write(io, $"{Cyan(color, svc.Name)}{Dim(color, $"  ({methodCount} method{(methodCount != 1 ? "s" : "")})")}");

            if (cli.Verbose)
            {
                foreach (var method in svc.Methods)
                {
                    var tag = method.MethodType switch
                    {
                        "Unary" => "",
                        "ServerStreaming" => Dim(color, " [server-streaming]"),
                        "ClientStreaming" => Dim(color, " [client-streaming]"),
                        "Duplex" => Dim(color, " [duplex]"),
                        _ => ""
                    };
                    Write(io, $"  {method.Name}{tag}");
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// <c>bowire discover</c> (#534) — probe a URL with every loaded
    /// protocol plugin and report what each one found, or why it didn't.
    /// <para>
    /// Deliberately NOT folded into <c>bowire list</c>: that command is
    /// documented as gRPC-reflection-only and hard-wired to
    /// <see cref="GrpcReflectionClient"/>, so widening it would silently
    /// change what existing scripts get back. This one shares
    /// <see cref="BowireDiscoveryProbe"/> with the <c>/api/services</c>
    /// endpoint and the <c>bowire.discover</c> MCP tool, so the workbench
    /// and the terminal can never drift on the diagnosis.
    /// </para>
    /// </summary>
    private static async Task<int> DiscoverImplAsync(CliCommandOptions cli, CommandIo io)
    {
        // `rest@https://…` narrows the fanout to one plugin — same hint
        // grammar the sidebar and /api/services accept. #538 moved the
        // split into BowireCli.BuildCliOptions so there is exactly one
        // BowireServerUrl.Parse call site on the CLI; cli.Url is already
        // bare here and cli.Protocol carries the hint.
        var pluginHint = cli.Protocol;

        // Plugin assemblies are already in the AppDomain: Program.cs runs
        // the injected IBowirePluginLoader before subcommand dispatch.
        var registry = BowireProtocolRegistry.Discover();
        var probe = await BowireDiscoveryProbe.RunAsync(
            registry, cli.Url, pluginHint,
            showInternalServices: false,
            perProbeCeiling: TimeSpan.FromSeconds(8)).ConfigureAwait(false);

        var color = UseColor(io.Out);

        foreach (var svc in probe.Services)
        {
            var methodCount = svc.Methods.Count;
            Write(io, $"{Cyan(color, svc.Name)}"
                + $"{Dim(color, $"  ({methodCount} method{(methodCount != 1 ? "s" : "")}, via {svc.Source})")}");

            if (cli.Verbose)
            {
                foreach (var method in svc.Methods)
                    Write(io, $"  {method.Name}");
            }
        }

        if (probe.Services.Count > 0) Write(io, "");

        // The attempt table is the point of the command — there is no
        // collapsed-vs-expanded tradeoff on a terminal, so it always
        // prints in full. Failures first: that is what the operator came
        // for when the service list above is empty.
        if (probe.Attempts.Count == 0)
        {
            WriteWarning(io, pluginHint is null
                ? "No protocol plugins are loaded."
                : $"No plugin registered for hint '{pluginHint}'.");
            return 1;
        }

        var ordered = probe.Attempts.OrderBy(OutcomeRank).ThenBy(a => a.Plugin, StringComparer.Ordinal).ToList();
        var pluginWidth = ordered.Max(a => a.Plugin.Length);
        var failed = ordered.Count(a => a.Outcome
            is BowireDiscoveryAttempt.OutcomeError or BowireDiscoveryAttempt.OutcomeTimeout);
        // `partial` gets its own term rather than folding into `failed`
        // (#544): a partial probe DID contribute services, and counting it
        // as a failure next to a list that visibly has entries reads as a
        // bug in the tool.
        var partial = ordered.Count(a => a.Outcome == BowireDiscoveryAttempt.OutcomePartial);
        Write(io, Dim(color, $"{ordered.Count} plugin{(ordered.Count != 1 ? "s" : "")} probed"
            + (partial > 0 ? $" · {partial} partial" : "")
            + $" · {failed} failed"));
        foreach (var a in ordered)
        {
            Write(io, "  "
                + a.Plugin.PadRight(pluginWidth)
                // 7 fits every value in the vocabulary: `timeout` already
                // forced that width and `partial` is exactly as long.
                + "  " + a.Outcome.PadRight(7)
                + "  " + Dim(color, $"{a.DurationMs} ms".PadLeft(8))
                + "  " + a.Message);
        }

        if (partial > 0)
        {
            Write(io, "");
            Write(io, Dim(color, $"{partial} plugin{(partial != 1 ? "s" : "")} returned partial results"
                + " — the services above are incomplete."));
        }

        // Unchanged on purpose: a partial probe found services, so the
        // documented "exit 1 when nothing was found" CI gate stays green.
        // Gating on partial is an opt-in flag's job, not a silent change.
        return probe.Services.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// Sort key for the attempt table — failures first, successes last,
    /// so the reason a discovery came back empty is the first thing on
    /// screen. Mirrors the row order the workbench's diagnostics
    /// disclosure uses.
    /// </summary>
    private static int OutcomeRank(BowireDiscoveryAttempt attempt) => attempt.Outcome switch
    {
        BowireDiscoveryAttempt.OutcomeError => 0,
        BowireDiscoveryAttempt.OutcomeTimeout => 1,
        // Above `empty`: a plugin that found things and still faulted is
        // closer to the reason the operator ran this than nine rows of
        // "returned no services".
        BowireDiscoveryAttempt.OutcomePartial => 2,
        BowireDiscoveryAttempt.OutcomeEmpty => 3,
        _ => 4,
    };

    private static async Task<int> DescribeImplAsync(CliCommandOptions cli, CommandIo io)
    {
        if (cli.Target is null)
        {
            WriteError(io, "Usage: bowire describe --url <url> <service>[/<method>]");
            return 2;
        }

        using var client = new GrpcReflectionClient(cli.Url, showInternalServices: false);

        // Check if target contains a method name (service/method)
        if (cli.Target.Contains('/'))
        {
            var parts = cli.Target.Split('/', 2);
            var serviceName = parts[0];
            var methodName = parts[1];

            var services = await client.ListServicesAsync();
            var svc = services.FirstOrDefault(s => s.Name == serviceName);
            if (svc is null)
            {
                WriteError(io, $"Service '{serviceName}' not found.");
                return 2;
            }

            var method = svc.Methods.FirstOrDefault(m => m.Name == methodName);
            if (method is null)
            {
                WriteError(io, $"Method '{methodName}' not found in service '{serviceName}'.");
                return 2;
            }

            DescribeMethod(io, method, detailed: true);
        }
        else
        {
            var services = await client.ListServicesAsync();
            var svc = services.FirstOrDefault(s => s.Name == cli.Target);
            if (svc is null)
            {
                WriteError(io, $"Service '{cli.Target}' not found.");
                return 2;
            }

            DescribeService(io, svc);
        }

        return 0;
    }

    /// <summary>
    /// <c>bowire call</c>. Two code paths on purpose (#538):
    /// <list type="bullet">
    ///   <item>
    ///     gRPC unary/auto-streaming keeps the original
    ///     <see cref="GrpcReflectionClient"/> + <see cref="GrpcInvoker"/>
    ///     body verbatim. It is the existing scripting audience's hot path
    ///     and it must not start paying for
    ///     <c>BowireProtocolRegistry.Discover()</c>'s assembly scan.
    ///   </item>
    ///   <item>
    ///     Everything else (an explicit non-grpc protocol, or
    ///     <c>--stream</c>) goes through
    ///     <see cref="InvokeViaRegistryAsync"/>, which shares
    ///     <see cref="BowireDiscoveryProbe"/> with <c>bowire discover</c>,
    ///     <c>/api/services</c> and the MCP tool.
    ///   </item>
    /// </list>
    /// </summary>
    private static async Task<int> CallImplAsync(CliCommandOptions cli, CommandIo io, CancellationToken ct)
    {
        if (cli.Target is null || !cli.Target.Contains('/'))
        {
            WriteError(io, "Usage: bowire call --url <url> <service>/<method> -d '<json>'");
            return 2;
        }

        var parts = cli.Target.Split('/', 2);
        var serviceName = parts[0];
        var methodName = parts[1];

        // Default to an empty JSON object when the user doesn't pass -d.
        // Unary calls take the first message; client-streaming calls
        // carry every -d as a separate frame.
        var messages = cli.Data.Count > 0 ? new List<string>(cli.Data) : ["{}"];

        // Expand @filename references in place so downstream invokers
        // see the concrete payload.
        for (var i = 0; i < messages.Count; i++)
        {
            if (!messages[i].StartsWith('@')) continue;
            var filePath = messages[i][1..];
            if (!File.Exists(filePath))
            {
                WriteError(io, $"File not found: {filePath}");
                return 1;
            }
            messages[i] = await File.ReadAllTextAsync(filePath, ct);
        }

        // Parse metadata headers "key: value"
        Dictionary<string, string>? metadata = null;
        if (cli.Headers.Count > 0)
        {
            metadata = new Dictionary<string, string>();
            foreach (var h in cli.Headers)
            {
                var colonIdx = h.IndexOf(':', StringComparison.Ordinal);
                if (colonIdx > 0)
                {
                    var key = h[..colonIdx].Trim();
                    var value = h[(colonIdx + 1)..].Trim();
                    metadata[key] = value;
                }
            }
        }

        // #538 — {{name}} / ${name} resolution over body, URL and metadata.
        // Same resolver + same --env-file-then---var precedence the Flow
        // runner uses, so a request copied out of the workbench behaves the
        // same in `bowire call` as it does in `bowire test`. Resolve()
        // short-circuits when a string carries no placeholder, so a payload
        // without variables is byte-identical to what pre-#538 sent.
        var env = BuildCallVars(cli, io, out var envFileError);
        if (envFileError) return 2;
        for (var i = 0; i < messages.Count; i++)
            messages[i] = FlowVariableResolver.Resolve(messages[i], env);
        cli.Url = FlowVariableResolver.Resolve(cli.Url, env);
        if (metadata is not null)
        {
            foreach (var key in metadata.Keys.ToList())
                metadata[key] = FlowVariableResolver.Resolve(metadata[key], env);
        }

        // Anything the gRPC invoker can't express routes through the
        // plugin registry. `--protocol grpc` without --stream stays on the
        // fast path: it means the same thing, it just says so out loud.
        var wantsRegistry = cli.Stream
            || (cli.Protocol is not null
                && !string.Equals(cli.Protocol, "grpc", StringComparison.OrdinalIgnoreCase));
        if (wantsRegistry)
            return await InvokeViaRegistryAsync(cli, io, serviceName, methodName, messages, metadata, ct);

        using var reflectionClient = new GrpcReflectionClient(cli.Url, showInternalServices: false);
        using var invoker = new GrpcInvoker(cli.Url, reflectionClient);

        // Try unary first, then streaming
        var result = await invoker.InvokeUnaryAsync(serviceName, methodName, messages, metadata);

        if (result.Status == "Use the streaming endpoint for server-streaming and duplex calls.")
        {
            // Server streaming or duplex -- use streaming invocation.
            // The CLI only needs the JSON rendering; the binary side of
            // the frame is for the mock-server recorder path.
            await foreach (var frame in invoker.InvokeStreamingWithFramesAsync(
                serviceName, methodName, messages, metadata))
            {
                WriteJsonResponse(io, frame.Json, cli.Compact);
            }
            return 0;
        }

        if (result.Status != "OK")
        {
            WriteError(io, $"gRPC error: {result.Status}");
            if (result.Response is not null)
                WriteError(io, $"  {result.Response}");

            if (result.Metadata.Count > 0)
            {
                WriteError(io, "  Trailers:");
                foreach (var entry in result.Metadata)
                    WriteError(io, $"    {entry.Key}: {entry.Value}");
            }

            return 2;
        }

        // Print response
        if (result.Response is not null)
            WriteJsonResponse(io, result.Response, cli.Compact);

        // Print timing to stderr (so it doesn't interfere with piped output).
        // Only suppress for production-Console stderr when the OS reports
        // a redirect; the test-supplied StringWriter falls through and
        // always receives the timing line.
        if (!ReferenceEquals(io.Err, Console.Error) || !Console.IsErrorRedirected)
            await io.Err.WriteLineAsync(Dim(UseColor(io.Err), $"  {result.DurationMs}ms")).ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    /// The protocol-generic half of <c>bowire call</c> (#538). Discovery
    /// is delegated to <see cref="BowireDiscoveryProbe"/> — the same
    /// fan-out <c>/api/services</c>, <c>bowire discover</c> and the
    /// <c>bowire.discover</c> MCP tool use — so the terminal can never
    /// disagree with the workbench about which plugin owns a URL. Only
    /// the invoke half lives here.
    /// <para>
    /// Two reasons the probe is not optional even when the plugin is
    /// pinned: several plugins populate a schema cache during
    /// <c>DiscoverAsync</c> that <c>InvokeAsync</c> then reads (the
    /// pattern <c>FlowTestRunner.RunStepAsync</c> follows), and the
    /// attempt records are what turn "nothing happened" into a printable
    /// reason.
    /// </para>
    /// </summary>
    private static async Task<int> InvokeViaRegistryAsync(
        CliCommandOptions cli, CommandIo io,
        string serviceName, string methodName,
        List<string> messages, Dictionary<string, string>? metadata,
        CancellationToken ct)
    {
        // Plugin assemblies are already in the AppDomain: Program.cs runs
        // the injected IBowirePluginLoader before subcommand dispatch.
        var registry = BowireProtocolRegistry.Discover();
        if (registry.Protocols.Count == 0)
        {
            WriteError(io, "No protocol plugins are loaded.");
            WriteError(io, "  Install one with: bowire plugin install Kuestenlogik.Bowire.Protocol.Rest");
            return 2;
        }

        // GetById matches ordinally; the CLI is case-insensitive
        // everywhere else a plugin id appears, so resolve it here.
        IBowireProtocol? pinned = null;
        if (cli.Protocol is not null)
        {
            pinned = registry.Protocols.FirstOrDefault(p =>
                string.Equals(p.Id, cli.Protocol, StringComparison.OrdinalIgnoreCase));
            if (pinned is null)
            {
                WriteError(io, $"Unknown protocol '{cli.Protocol}'.");
                WriteError(io, "  Loaded plugins: "
                    + string.Join(", ", registry.Protocols.Select(p => p.Id).Order(StringComparer.Ordinal)));
                WriteError(io, "  Install more with: bowire plugin install Kuestenlogik.Bowire.Protocol.<Name>");
                return 2;
            }
        }

        var probe = await BowireDiscoveryProbe.RunAsync(
            registry, cli.Url, pinned?.Id,
            showInternalServices: false,
            perProbeCeiling: TimeSpan.FromSeconds(8),
            logger: null, ct: ct).ConfigureAwait(false);

        // Unpinned: the plugin that actually found the target service
        // wins. Falling back to "the first plugin that found anything"
        // would invoke against a plugin that has never heard of the
        // method, which fails later and less legibly.
        var protocol = pinned ?? ResolveProtocolForService(registry, probe, serviceName);
        if (protocol is null)
        {
            WriteError(io, $"No loaded plugin found service '{serviceName}' at {cli.Url}.");
            foreach (var attempt in probe.Attempts.OrderBy(OutcomeRank).ThenBy(a => a.Plugin, StringComparer.Ordinal))
                WriteError(io, $"  {attempt.Plugin}: {attempt.Outcome} — {attempt.Message}");
            WriteError(io, "  Pin the plugin with --protocol <id> or the protocol@url form,"
                + " or run `bowire discover --url " + cli.Url + "` for the full table.");
            return 2;
        }

        if (cli.Stream)
            return await StreamViaProtocolAsync(cli, io, protocol, serviceName, methodName, messages, metadata, ct);

        InvokeResult result;
        try
        {
            result = await protocol.InvokeAsync(cli.Url, serviceName, methodName, messages,
                showInternalServices: false, metadata: metadata, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (PluginBoundary.NonFatal(ex))
        {
            WriteError(io, $"{protocol.Name} invocation failed: {ex.Message}");
            return 1;
        }

        // Plugins report their native status label — an HTTP code name for
        // REST, a gRPC status name for gRPC. "OK" and any 2xx are success;
        // everything else is a call the operator should be able to gate CI on.
        if (!IsSuccessStatus(result.Status))
        {
            WriteError(io, $"{protocol.Name} error: {result.Status}");
            if (result.Response is not null)
                WriteError(io, $"  {result.Response}");
            if (result.Metadata.Count > 0)
            {
                WriteError(io, "  Headers:");
                foreach (var entry in result.Metadata)
                    WriteError(io, $"    {entry.Key}: {entry.Value}");
            }
            return 2;
        }

        if (result.Response is not null)
            WriteJsonResponse(io, result.Response, cli.Compact);

        if (!ReferenceEquals(io.Err, Console.Error) || !Console.IsErrorRedirected)
            await io.Err.WriteLineAsync(Dim(UseColor(io.Err), $"  {result.DurationMs}ms")).ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    /// <c>--stream</c>: one JSON document per frame until the server ends
    /// the stream or the operator interrupts. Split out of
    /// <see cref="InvokeViaRegistryAsync"/> so the <c>await foreach</c>
    /// and its two distinct failure modes stay readable.
    /// </summary>
    private static async Task<int> StreamViaProtocolAsync(
        CliCommandOptions cli, CommandIo io, IBowireProtocol protocol,
        string serviceName, string methodName,
        List<string> messages, Dictionary<string, string>? metadata,
        CancellationToken ct)
    {
        var frames = 0;
        try
        {
            await foreach (var frame in protocol.InvokeStreamAsync(cli.Url, serviceName, methodName,
                messages, showInternalServices: false, metadata: metadata, ct: ct).ConfigureAwait(false))
            {
                frames++;
                WriteJsonResponse(io, frame, cli.Compact);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ctrl+C on a subscription is the normal way to stop, not an
            // error. Whatever arrived is already on stdout.
            return 0;
        }
        catch (NotSupportedException)
        {
            // A one-liner, not a stack trace: several plugins declare
            // InvokeStreamAsync only to throw, and "this protocol has no
            // stream" is a usage answer, not a crash.
            WriteError(io, $"The {protocol.Name} plugin does not support streaming invocations.");
            WriteError(io, "  Drop --stream to invoke it as a single request.");
            return 2;
        }
        catch (Exception ex) when (PluginBoundary.NonFatal(ex))
        {
            // The count is the diagnosis: "0 frames" is a connect/subscribe
            // failure, "n frames" is a stream that died mid-flight. Stated
            // as a value rather than a pluralised sentence because the
            // analyzer can't see through the async iterator and flags any
            // comparison on `frames` as dead code.
            WriteError(io, $"{protocol.Name} stream failed (frames received: {frames}): {ex.Message}");
            return 1;
        }

        // A stream that ends without ever delivering a frame is the one
        // outcome that would otherwise print nothing and exit 0 — the
        // worst possible answer for a CI gate, and indistinguishable from
        // success in a pipeline. Say what happened and fail, the same way
        // `bowire discover` exits 1 when it found no service.
        if (frames == 0)
        {
            WriteWarning(io, $"The {protocol.Name} stream ended without delivering a frame.");
            WriteWarning(io, "  Either the method isn't a streaming one (drop --stream), "
                + "or the subscription matched nothing.");
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// Pick the plugin that discovered <paramref name="serviceName"/>.
    /// <see cref="BowireDiscoveryProbe"/> tags every service with the
    /// plugin that produced it, so this is a lookup rather than a second
    /// heuristic. Falls back to the sole plugin that found anything, which
    /// covers plugins whose service naming doesn't survive a round-trip
    /// through a hand-typed target.
    /// </summary>
    private static IBowireProtocol? ResolveProtocolForService(
        BowireProtocolRegistry registry, BowireDiscoveryProbeResult probe, string serviceName)
    {
        var match = probe.Services.FirstOrDefault(s =>
            string.Equals(s.Name, serviceName, StringComparison.OrdinalIgnoreCase));
        var sourceId = match?.Source;
        if (string.IsNullOrEmpty(sourceId))
        {
            var producers = probe.Attempts
                .Where(a => a.ServicesFound > 0)
                .Select(a => a.PluginId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (producers.Count != 1) return null;
            sourceId = producers[0];
        }
        return registry.Protocols.FirstOrDefault(p =>
            string.Equals(p.Id, sourceId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Success across the whole plugin surface: gRPC's <c>"OK"</c>, REST's
    /// numeric or named HTTP status, and the plugins that report a bare
    /// empty status for fire-and-forget publishes.
    /// </summary>
    private static bool IsSuccessStatus(string status)
    {
        if (string.IsNullOrEmpty(status)) return true;
        if (status.Equals("OK", StringComparison.OrdinalIgnoreCase)) return true;
        if (int.TryParse(status, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var code))
            return code is >= 200 and < 400;
        // Named HTTP statuses arrive as the enum name ("NoContent",
        // "Accepted", …); anything 4xx/5xx-shaped is a failure name.
        return status.Equals("Created", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Accepted", StringComparison.OrdinalIgnoreCase)
            || status.Equals("NoContent", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Build the <c>{{name}}</c> resolver's variable map from
    /// <c>--env-file</c> (in CLI order) then <c>--var</c>, reusing the
    /// Flow runner's parsers so the two commands cannot drift on the
    /// KEY=VALUE grammar. Sets <paramref name="fileError"/> and prints
    /// when a file can't be read — a silently-empty variable map produces
    /// a request with literal <c>{{token}}</c> in it, which is far worse
    /// than failing.
    /// </summary>
    private static Dictionary<string, string> BuildCallVars(
        CliCommandOptions cli, CommandIo io, out bool fileError)
    {
        fileError = false;
        var pairs = new List<string>();
        foreach (var file in cli.VarFiles)
        {
            try
            {
                pairs.AddRange(FlowTestRunner.ReadEnvFileLines(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                WriteError(io, $"Failed to read --env-file '{file}': {ex.Message}");
                fileError = true;
                return [];
            }
        }
        pairs.AddRange(cli.Vars);
        return FlowTestRunner.MergeEnv(pairs);
    }

    private static void WriteJsonResponse(CommandIo io, string json, bool compact)
    {
        if (compact)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                io.OutLine(JsonSerializer.Serialize(doc.RootElement, CompactJson));
            }
            catch
            {
                io.OutLine(json);
            }
        }
        else
        {
            io.OutLine(json);
        }
    }

    private static void DescribeService(CommandIo io, BowireServiceInfo svc)
    {
        var color = UseColor(io.Out);
        Write(io, $"{Bold(color, Cyan(color, svc.Name))}");
        if (!string.IsNullOrEmpty(svc.Package))
            Write(io, $"{Dim(color, $"  package: {svc.Package}")}");
        Write(io, "");

        foreach (var method in svc.Methods)
            DescribeMethod(io, method, detailed: false);
    }

    private static void DescribeMethod(CommandIo io, BowireMethodInfo method, bool detailed)
    {
        var color = UseColor(io.Out);
        var streamTag = method.MethodType switch
        {
            "Unary" => Dim(color, "unary"),
            "ServerStreaming" => Dim(color, "server-streaming"),
            "ClientStreaming" => Dim(color, "client-streaming"),
            "Duplex" => Dim(color, "duplex"),
            _ => Dim(color, method.MethodType)
        };

        Write(io, $"  {Bold(color, method.Name)} {streamTag}");
        Write(io, $"    {Dim(color, "rpc")} {method.Name}({Cyan(color, method.InputType.Name)}) {Dim(color, "returns")} ({Cyan(color, method.OutputType.Name)})");

        if (detailed)
        {
            Write(io, "");
            Write(io, $"  {Bold(color, "Request:")} {Cyan(color, method.InputType.FullName)}");
            DescribeMessage(io, method.InputType, indent: 4, visited: []);
            Write(io, "");
            Write(io, $"  {Bold(color, "Response:")} {Cyan(color, method.OutputType.FullName)}");
            DescribeMessage(io, method.OutputType, indent: 4, visited: []);
        }

        Write(io, "");
    }

    private static void DescribeMessage(CommandIo io, BowireMessageInfo msg, int indent, HashSet<string> visited)
    {
        if (msg.Fields.Count == 0)
            return;

        var color = UseColor(io.Out);
        if (!visited.Add(msg.FullName))
        {
            Write(io, $"{new string(' ', indent)}{Dim(color, $"(recursive: {msg.Name})")}");
            return;
        }

        foreach (var field in msg.Fields)
        {
            var prefix = new string(' ', indent);
            var label = field.IsRepeated ? "repeated " : field.IsMap ? "map " : "";
            var typeName = field.Type;

            if (field.MessageType is not null)
                typeName = Cyan(color, field.MessageType.Name);
            else if (field.EnumValues is not null)
                typeName = Cyan(color, field.Type);

            Write(io, $"{prefix}{Dim(color, label)}{typeName} {field.Name}{Dim(color, $" = {field.Number}")}");

            if (field.EnumValues is not null)
            {
                foreach (var ev in field.EnumValues)
                    Write(io, $"{prefix}  {Dim(color, $"{ev.Name} = {ev.Number}")}");
            }

            if (field.MessageType is not null && field.MessageType.Fields.Count > 0)
                DescribeMessage(io, field.MessageType, indent + 2, visited);
        }
    }


    // ---- Console formatting helpers ----

    private static void Write(CommandIo io, string text) => io.OutLine(text);

    private static void WriteError(CommandIo io, string text)
    {
        if (UseColor(io.Err))
            io.ErrLine($"\x1b[31m{text}\x1b[0m");
        else
            io.ErrLine(text);
    }

    private static void WriteWarning(CommandIo io, string text)
    {
        if (UseColor(io.Err))
            io.ErrLine($"\x1b[33m{text}\x1b[0m");
        else
            io.ErrLine(text);
    }

    private static string Cyan(bool useColor, string text) =>
        useColor ? $"\x1b[36m{text}\x1b[0m" : text;

    private static string Bold(bool useColor, string text) =>
        useColor ? $"\x1b[1m{text}\x1b[0m" : text;

    private static string Dim(bool useColor, string text) =>
        useColor ? $"\x1b[2m{text}\x1b[0m" : text;
}
