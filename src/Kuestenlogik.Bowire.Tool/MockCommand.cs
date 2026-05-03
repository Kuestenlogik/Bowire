// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Mock;
using Kuestenlogik.Bowire.Mock.Chaos;
using Kuestenlogik.Bowire.Mock.Loading;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.PluginLoading;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Handler for the <c>bowire mock</c> CLI subcommand — spins up a standalone
/// <see cref="MockServer"/> that replays a Bowire recording (or synthesises
/// one from an OpenAPI schema) as a real HTTP endpoint. CLI flags, env vars,
/// and <c>appsettings.json</c> (section <c>Bowire:Mock</c>) all feed the
/// same <see cref="MockCliOptions"/> binding — see
/// <c>BowireCli</c>.
/// </summary>
internal static class MockCommand
{
    public static async Task<int> RunAsync(MockCliOptions cli, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cli);

        var hasRecording = !string.IsNullOrEmpty(cli.RecordingPath);
        var hasSchema = !string.IsNullOrEmpty(cli.SchemaPath);
        var hasGrpcSchema = !string.IsNullOrEmpty(cli.GrpcSchemaPath);
        var hasGraphQlSchema = !string.IsNullOrEmpty(cli.GraphQlSchemaPath);
        var sourceCount = (hasRecording ? 1 : 0) + (hasSchema ? 1 : 0)
            + (hasGrpcSchema ? 1 : 0) + (hasGraphQlSchema ? 1 : 0);
        if (sourceCount != 1)
        {
            await Console.Error.WriteLineAsync(
                sourceCount == 0
                    ? "bowire mock: one of --recording <path>, --schema <path>, --grpc-schema <path>, or --graphql-schema <path> is required."
                    : "bowire mock: --recording, --schema, --grpc-schema, and --graphql-schema are mutually exclusive — pick one.");
            await Console.Error.WriteLineAsync("Run `bowire mock --help` for usage.").ConfigureAwait(false);
            return 2;
        }

        ChaosOptions chaos;
        try
        {
            chaos = string.IsNullOrEmpty(cli.Chaos) ? new ChaosOptions() : ChaosOptions.Parse(cli.Chaos);
        }
        catch (FormatException ex)
        {
            await Console.Error.WriteLineAsync("bowire mock: " + ex.Message);
            return 2;
        }

        try
        {
            // Load installed protocol plugins (if any) and pull their
            // mock-emitter contributions. Plugins that implement
            // `IBowireMockEmitter` (DIS, DDS, raw-UDP multicast, ...)
            // are discovered via the same ALC walk the workbench uses.
            // No-op when the plugin directory is empty. Resolves under
            // the same Bowire:PluginDir layering the host uses; argv
            // is no longer the source since BowireCli passes typed
            // values directly.
            var pluginDir = BowireConfiguration.PluginDir(BowireConfiguration.Build([]));
            PluginManager.LoadPlugins(pluginDir);
            var emitters = PluginManager.EnumeratePluginServices<Kuestenlogik.Bowire.Mocking.IBowireMockEmitter>();
            var transportHosts = PluginManager.EnumeratePluginServices<Kuestenlogik.Bowire.Mocking.IBowireMockTransportHost>();
            var schemaSources = PluginManager.EnumeratePluginServices<Kuestenlogik.Bowire.Mocking.IBowireMockSchemaSource>();
            var liveSchemaHandlers = PluginManager.EnumeratePluginServices<Kuestenlogik.Bowire.Mocking.IBowireMockLiveSchemaHandler>();
            var hostingExtensions = PluginManager.EnumeratePluginServices<Kuestenlogik.Bowire.Mocking.IBowireMockHostingExtension>();

            // Plugin detection — only meaningful for recording-driven
            // mocks; schema-only modes (--schema / --grpc-schema /
            // --graphql-schema) don't reference protocols that need
            // plugin code.
            if (hasRecording)
            {
                var detection = await DetectMissingPluginsAsync(cli.RecordingPath!, cli.Select, ct);
                if (detection.Missing.Count > 0)
                {
                    if (cli.AutoInstall)
                    {
                        var ok = await TryAutoInstallAsync(detection.Missing, pluginDir, ct);
                        if (!ok) return 1;
                        // Reload plugins so the freshly-installed
                        // protocols show up in subsequent registry walks
                        // (this also reseats the emitter list for
                        // proactive replay).
                        PluginManager.LoadPlugins(pluginDir);
                        emitters = PluginManager.EnumeratePluginServices<Kuestenlogik.Bowire.Mocking.IBowireMockEmitter>();
                        transportHosts = PluginManager.EnumeratePluginServices<Kuestenlogik.Bowire.Mocking.IBowireMockTransportHost>();
                        schemaSources = PluginManager.EnumeratePluginServices<Kuestenlogik.Bowire.Mocking.IBowireMockSchemaSource>();
                        liveSchemaHandlers = PluginManager.EnumeratePluginServices<Kuestenlogik.Bowire.Mocking.IBowireMockLiveSchemaHandler>();
                        hostingExtensions = PluginManager.EnumeratePluginServices<Kuestenlogik.Bowire.Mocking.IBowireMockHostingExtension>();
                    }
                    else
                    {
                        PrintMissingPlugins(detection.Missing);
                        return 1;
                    }
                }
            }

            var options = new MockServerOptions
            {
                RecordingPath = cli.RecordingPath,
                SchemaPath = cli.SchemaPath,
                GrpcSchemaPath = cli.GrpcSchemaPath,
                GraphQlSchemaPath = cli.GraphQlSchemaPath,
                Host = cli.Host,
                Port = cli.Port,
                Select = cli.Select,
                Watch = !cli.NoWatch,
                Chaos = chaos,
                // --stateful-once implies --stateful with wrap-around off;
                // either flag on its own is enough to enable stateful mode.
                Stateful = cli.Stateful || cli.StatefulOnce,
                StatefulWrapAround = !cli.StatefulOnce,
                CaptureMissPath = cli.CaptureMissPath,
                ReplaySpeed = cli.ReplaySpeed,
                ControlToken = cli.ControlToken,
                Loop = cli.Loop,
                Emitters = emitters,
                TransportHosts = transportHosts,
                SchemaSources = schemaSources,
                LiveSchemaHandlers = liveSchemaHandlers,
                HostingExtensions = hostingExtensions
                // TransportPorts left at default — per-transport ports
                // can be exposed as future CLI flags (e.g. --mqtt-port)
                // routed into this dictionary, but for now every host
                // picks its own default (MQTT plugin defaults to 1883)
                // or an OS-assigned port when 0 is configured.
            };

            await using var server = await MockServer.StartAsync(options, ct);

            // Graceful Ctrl+C: flip the cancellation token into the server
            // so it stops its Kestrel + watcher before the process exits.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            await server.WaitForShutdownAsync(cts.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("bowire mock: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Load the recording, snapshot the loaded protocol ids, and
    /// compute the set of missing plugins. The recording load is
    /// paid for again inside <see cref="MockServer.StartAsync"/> —
    /// that's a single file read + JSON parse, deliberate trade-off
    /// to keep the detection layer free of any
    /// <see cref="MockServer"/> coupling.
    /// </summary>
    private static Task<DetectionResult> DetectMissingPluginsAsync(
        string recordingPath, string? select, CancellationToken ct)
    {
        _ = ct;
        var recording = RecordingLoader.Load(recordingPath, select);
        var registry = BowireProtocolRegistry.Discover();
        var loadedIds = registry.Protocols.Select(p => p.Id);
        var missing = MissingPluginDetector.Detect(recording, loadedIds);
        return Task.FromResult(new DetectionResult(missing));
    }

    /// <summary>
    /// Render a friendly multi-line error explaining which plugins
    /// the recording referenced but the host doesn't have, with the
    /// install command for each. <c>bowire plugin install …</c> is
    /// suggested first; the corresponding NuGet package id is shown
    /// as a fallback for embedded-host users.
    /// </summary>
    private static void PrintMissingPlugins(IReadOnlyList<MissingPlugin> missing)
    {
        var word = missing.Count == 1 ? "protocol" : "protocols";
        Console.Error.WriteLine();
        Console.Error.WriteLine($"✗ Recording references {missing.Count} {word} whose plugin{(missing.Count == 1 ? " is" : "s are")} not installed:");
        foreach (var m in missing)
        {
            var pkg = m.SuggestedPackageId ?? "(unknown — third-party plugin?)";
            Console.Error.WriteLine($"    • {m.ProtocolId,-12} → {pkg}");
        }
        Console.Error.WriteLine();
        var resolvable = missing.Where(m => m.SuggestedPackageId is not null).ToList();
        if (resolvable.Count > 0)
        {
            Console.Error.WriteLine("  Install with:");
            foreach (var m in resolvable)
            {
                Console.Error.WriteLine($"    bowire plugin install {m.SuggestedPackageId}");
            }
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Or re-run with --auto-install to fetch them now.");
        }
        else
        {
            Console.Error.WriteLine("  These protocol ids are not in Bowire's catalogue. If they're third-party");
            Console.Error.WriteLine("  plugins, install the matching NuGet package via `bowire plugin install`.");
        }
        Console.Error.WriteLine();
    }

    private sealed record DetectionResult(IReadOnlyList<MissingPlugin> Missing);

    /// <summary>
    /// Resolve and install every missing plugin that has a known NuGet
    /// package id. Returns true when all installs succeeded; false when
    /// any failed (or any of the missing protocols had no suggested
    /// package because they're third-party plugins outside Bowire's
    /// catalogue). Continues past per-package failures so the caller
    /// gets a complete tally before bailing.
    /// </summary>
    private static async Task<bool> TryAutoInstallAsync(
        IReadOnlyList<MissingPlugin> missing,
        string? pluginDir,
        CancellationToken ct)
    {
        var unknown = missing.Where(m => m.SuggestedPackageId is null).ToList();
        if (unknown.Count > 0)
        {
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync("✗ --auto-install can't help with these — they're not in Bowire's catalogue:");
            foreach (var m in unknown) await Console.Error.WriteLineAsync($"    • {m.ProtocolId}");
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync("  Install the matching NuGet package manually with:");
            await Console.Error.WriteLineAsync("    bowire plugin install <package-id>");
            await Console.Error.WriteLineAsync();
            return false;
        }

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"→ Installing {missing.Count} missing protocol plugin{(missing.Count == 1 ? "" : "s")}…");
        var failures = 0;
        foreach (var m in missing)
        {
            // version=null + sources=null → InstallAsync uses the
            // default NuGet feed (nuget.org) plus whatever
            // appsettings/env-var configuration the host already has.
            var exit = await PluginManager.InstallAsync(
                packageId: m.SuggestedPackageId!,
                version: null,
                pluginDir: pluginDir,
                sources: null,
                ct: ct);
            if (exit != 0) failures++;
        }
        await Console.Out.WriteLineAsync();
        if (failures > 0)
        {
            await Console.Error.WriteLineAsync($"✗ {failures} of {missing.Count} install{(missing.Count == 1 ? "" : "s")} failed. See output above.");
            await Console.Error.WriteLineAsync();
            return false;
        }
        await Console.Out.WriteLineAsync($"✓ All {missing.Count} plugin{(missing.Count == 1 ? "" : "s")} installed. Continuing with mock startup…");
        await Console.Out.WriteLineAsync();
        return true;
    }

}
