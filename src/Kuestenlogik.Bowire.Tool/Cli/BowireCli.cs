// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Mcp;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// Top-level CLI dispatcher built on <c>System.CommandLine</c> 2.0.7.
/// Every subcommand declares typed <see cref="Option{T}"/> instances
/// whose <c>DefaultValueFactory</c> reads from the shared
/// <see cref="IConfiguration"/> — so env vars (<c>BOWIRE_*</c>),
/// <c>appsettings.json</c> sections (<c>Bowire:Mock</c>,
/// <c>Bowire:Cli</c>, <c>Bowire:Test</c>, <c>Bowire:Plugin</c>), and
/// CLI flags layer in standard .NET precedence (cfg → flag wins).
///
/// <para>
/// Action handlers receive parsed values directly and construct the
/// existing <see cref="MockCliOptions"/> / <see cref="CliCommandOptions"/> /
/// <see cref="TestCliOptions"/> / <see cref="PluginCliOptions"/> instances
/// for the legacy entry points. Replaces every <c>BowireConfiguration.Build*Options</c>
/// helper plus the hand-rolled <c>GetOption</c>/<c>ExtractRepeated</c>/
/// <c>ExtractPositional</c> scattered across the tool.
/// </para>
/// </summary>
internal static class BowireCli
{
    public static async Task<int> RunAsync(string[] args, IConfiguration cfg, string pluginDir)
    {
        var root = BuildRoot(args, cfg, pluginDir);
        return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
    }

    private static RootCommand BuildRoot(string[] originalArgs, IConfiguration cfg, string pluginDir)
    {
        var root = new RootCommand(
            "Bowire — multi-protocol API workbench. Run without a subcommand to launch the browser UI; " +
            "run a subcommand (list / describe / call / mock / mcp / plugin / test) for scripting.");

        // Root-level options describe the browser-UI defaults so
        // `bowire --port 6000 --url http://api.local` resolves through
        // the typed parser. The default action delegates to
        // BrowserUiHost which still binds via BowireConfiguration to
        // pick up multi-URL and url-file support natively.
        root.Add(new Option<string[]>("--url")
        {
            Description = "Server URL(s) to connect to. Repeat for multi-URL mode.",
            AllowMultipleArgumentsPerToken = true
        });
        root.Add(new Option<string>("--url-file") { Description = "File with server URLs (one per line)." });
        root.Add(new Option<int?>("--port") { Description = "Browser UI port. Default 5080." });
        root.Add(new Option<string>("--title") { Description = "Browser tab title. Default \"Bowire\"." });
        root.Add(new Option<string>("--description") { Description = "Subtitle shown below the title." });
        root.Add(new Option<bool>("--no-browser") { Description = "Don't auto-open a browser window." });
        root.Add(new Option<bool>("--enable-mcp-adapter") { Description = "Expose discovered services as MCP tools." });
        root.Add(new Option<bool>("--lock-server-url") { Description = "Disable URL editing in the UI." });
        root.Add(new Option<string>("--plugin-dir") { Description = "Override the plugin directory." });
        root.Add(new Option<string[]>("--disable-plugin")
        {
            Description = "Skip a protocol plugin at startup. Repeat or comma-separate ('--disable-plugin grpc --disable-plugin signalr' or '--disable-plugin grpc,signalr'). Useful when a plugin DLL won't load or its discovery probe is too slow for the current host.",
            AllowMultipleArgumentsPerToken = true,
        });

        root.SetAction(async (_, ct) =>
            await BrowserUiHost.RunAsync(originalArgs, cfg, pluginDir, ct).ConfigureAwait(false));

        root.Add(BuildListCommand(cfg));
        root.Add(BuildDescribeCommand(cfg));
        root.Add(BuildCallCommand(cfg));
        root.Add(BuildMockCommand(cfg));
        root.Add(BuildMcpCommand());
        root.Add(BuildPluginCommand(cfg, pluginDir));
        root.Add(BuildTestCommand(cfg));

        return root;
    }

    // -------------------- list / describe / call --------------------
    // CliCommandOptions binds Url / Plaintext / Verbose / Compact +
    // positional Target, repeated -d / -H. All three subcommands share
    // the option set; only the action handler differs.

    private static (Option<string> url, Option<bool> plaintext, Option<bool> verbose, Option<bool> compact,
        Option<string[]> data, Option<string[]> headers) GrpcCliOptions(IConfiguration cfg)
    {
        var url = new Option<string>("--url", "-url")
        {
            Description = "gRPC server URL.",
            DefaultValueFactory = _ => cfg["Bowire:Cli:Url"] ?? "https://localhost:5001"
        };
        var plaintext = new Option<bool>("-plaintext", "--plaintext")
        {
            Description = "Use plaintext (no TLS).",
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Cli:Plaintext")
        };
        var verbose = new Option<bool>("-v", "--verbose")
        {
            Description = "Verbose output (list).",
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Cli:Verbose")
        };
        var compact = new Option<bool>("--compact")
        {
            Description = "One-line JSON output (call, pipe-friendly).",
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Cli:Compact")
        };
        var data = new Option<string[]>("-d", "--data")
        { Description = "JSON body (or @filename). Repeatable for client-streaming." };
        var headers = new Option<string[]>("-H")
        { Description = "Metadata header \"key: value\". Repeatable." };
        return (url, plaintext, verbose, compact, data, headers);
    }

    private static CliCommandOptions BuildCliOptions(
        ParseResult pr,
        Option<string> url, Option<bool> plaintext, Option<bool> verbose, Option<bool> compact,
        Option<string[]>? data, Option<string[]>? headers, string? target)
    {
        var options = new CliCommandOptions
        {
            Url = pr.GetValue(url) ?? "https://localhost:5001",
            Plaintext = pr.GetValue(plaintext),
            Verbose = pr.GetValue(verbose),
            Compact = pr.GetValue(compact),
            Target = target
        };
        if (data is not null)
            options.Data.AddRange(pr.GetValue(data) ?? []);
        if (headers is not null)
            options.Headers.AddRange(pr.GetValue(headers) ?? []);
        // Plaintext URL downgrade: callers shouldn't have to do the
        // substitution themselves. Same one-line policy as
        // BowireConfiguration.BuildCliOptions used to apply.
        if (options.Plaintext && options.Url.StartsWith("https://", StringComparison.Ordinal))
            options.Url = "http://" + options.Url["https://".Length..];
        return options;
    }

    private static Command BuildListCommand(IConfiguration cfg)
    {
        var (url, plaintext, verbose, _, _, _) = GrpcCliOptions(cfg);
        var cmd = new Command("list", "List discovered gRPC services.");
        cmd.Add(url); cmd.Add(plaintext); cmd.Add(verbose);
        cmd.SetAction(async (pr, _) =>
            await CliHandler.ListAsync(BuildCliOptions(pr, url, plaintext, verbose, new Option<bool>("--compact"), null, null, null)).ConfigureAwait(false));
        return cmd;
    }

    private static Command BuildDescribeCommand(IConfiguration cfg)
    {
        var (url, plaintext, verbose, _, _, _) = GrpcCliOptions(cfg);
        var target = new Argument<string>("target") { Description = "Service name, or service/method." };

        var cmd = new Command("describe", "Describe a gRPC service or method.");
        cmd.Add(target); cmd.Add(url); cmd.Add(plaintext); cmd.Add(verbose);
        cmd.SetAction(async (pr, _) =>
            await CliHandler.DescribeAsync(BuildCliOptions(
                pr, url, plaintext, verbose, new Option<bool>("--compact"), null, null, pr.GetValue(target))).ConfigureAwait(false));
        return cmd;
    }

    private static Command BuildCallCommand(IConfiguration cfg)
    {
        var (url, plaintext, verbose, compact, data, headers) = GrpcCliOptions(cfg);
        var target = new Argument<string>("target") { Description = "service/method." };

        var cmd = new Command("call", "Invoke a gRPC method (grpcurl-style).");
        cmd.Add(target); cmd.Add(url); cmd.Add(plaintext); cmd.Add(verbose);
        cmd.Add(compact); cmd.Add(data); cmd.Add(headers);
        cmd.SetAction(async (pr, _) =>
            await CliHandler.CallAsync(BuildCliOptions(
                pr, url, plaintext, verbose, compact, data, headers, pr.GetValue(target))).ConfigureAwait(false));
        return cmd;
    }

    // -------------------- mock --------------------

    private static Command BuildMockCommand(IConfiguration cfg)
    {
        var recording = new Option<string?>("--recording", "-r")
        {
            Description = "Path to a Bowire recording JSON.",
            DefaultValueFactory = _ => cfg["Bowire:Mock:RecordingPath"]
        };
        var schema = new Option<string?>("--schema")
        {
            Description = "Path to an OpenAPI 3 document for schema-only mocks.",
            DefaultValueFactory = _ => cfg["Bowire:Mock:SchemaPath"]
        };
        var grpcSchema = new Option<string?>("--grpc-schema")
        {
            Description = "Path to a protobuf FileDescriptorSet (.pb).",
            DefaultValueFactory = _ => cfg["Bowire:Mock:GrpcSchemaPath"]
        };
        var graphqlSchema = new Option<string?>("--graphql-schema")
        {
            Description = "Path to a GraphQL SDL file.",
            DefaultValueFactory = _ => cfg["Bowire:Mock:GraphQlSchemaPath"]
        };
        var port = new Option<int>("--port")
        {
            Description = "Listen port. Default 6000.",
            DefaultValueFactory = _ => cfg.GetValue<int?>("Bowire:Mock:Port") ?? 6000
        };
        var host = new Option<string>("--host")
        {
            Description = "Listen host. Default 127.0.0.1.",
            DefaultValueFactory = _ => cfg["Bowire:Mock:Host"] ?? "127.0.0.1"
        };
        var select = new Option<string?>("--select")
        {
            Description = "Disambiguator when the recording file contains multiple recordings.",
            DefaultValueFactory = _ => cfg["Bowire:Mock:Select"]
        };
        var noWatch = new Option<bool>("--no-watch")
        {
            Description = "Disable hot-reload on file changes.",
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Mock:NoWatch")
        };
        var stateful = new Option<bool>("--stateful")
        {
            Description = "Stateful cursor mode (each request advances).",
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Mock:Stateful")
        };
        var statefulOnce = new Option<bool>("--stateful-once")
        {
            Description = "Stateful + no wrap-around at the end of the recording.",
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Mock:StatefulOnce")
        };
        var loop = new Option<bool>("--loop")
        {
            Description = "Loop proactive emitters indefinitely.",
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Mock:Loop")
        };
        var autoInstall = new Option<bool>("--auto-install")
        {
            Description = "Auto-install missing protocol plugins.",
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Mock:AutoInstall")
        };
        var chaos = new Option<string?>("--chaos")
        {
            Description = "Chaos injection: e.g. \"latency:100-500,fail-rate:0.05\".",
            DefaultValueFactory = _ => cfg["Bowire:Mock:Chaos"]
        };
        var captureMiss = new Option<string?>("--capture-miss")
        {
            Description = "Persist unmatched requests to this file.",
            DefaultValueFactory = _ => cfg["Bowire:Mock:CaptureMissPath"]
        };
        var controlToken = new Option<string?>("--control-token")
        {
            Description = "Auth token for the runtime-scenario-switch control endpoint.",
            DefaultValueFactory = _ => cfg["Bowire:Mock:ControlToken"]
        };

        var cmd = new Command("mock", "Replay a recording (or schema) as a local API endpoint.");
        cmd.Add(recording); cmd.Add(schema); cmd.Add(grpcSchema); cmd.Add(graphqlSchema);
        cmd.Add(port); cmd.Add(host); cmd.Add(select); cmd.Add(noWatch);
        cmd.Add(stateful); cmd.Add(statefulOnce); cmd.Add(loop); cmd.Add(autoInstall);
        cmd.Add(chaos); cmd.Add(captureMiss); cmd.Add(controlToken);
        cmd.SetAction(async (pr, ct) =>
        {
            var options = new MockCliOptions
            {
                RecordingPath = pr.GetValue(recording),
                SchemaPath = pr.GetValue(schema),
                GrpcSchemaPath = pr.GetValue(grpcSchema),
                GraphQlSchemaPath = pr.GetValue(graphqlSchema),
                Host = pr.GetValue(host) ?? "127.0.0.1",
                Port = pr.GetValue(port),
                Select = pr.GetValue(select),
                NoWatch = pr.GetValue(noWatch),
                Stateful = pr.GetValue(stateful),
                StatefulOnce = pr.GetValue(statefulOnce),
                Loop = pr.GetValue(loop),
                AutoInstall = pr.GetValue(autoInstall),
                Chaos = pr.GetValue(chaos),
                CaptureMissPath = pr.GetValue(captureMiss),
                ControlToken = pr.GetValue(controlToken)
            };
            return await MockCommand.RunAsync(options, ct).ConfigureAwait(false);
        });
        return cmd;
    }

    // -------------------- mcp (typed since the SDK migration) --------------------

    private static Command BuildMcpCommand()
    {
        var bind = new Option<string>("--bind")
        {
            Description = "Transport for the MCP server.",
            DefaultValueFactory = _ => "stdio"
        };
        bind.AcceptOnlyFromAmong("stdio", "http");

        var port = new Option<int>("--port")
        {
            Description = "Port for --bind http.",
            DefaultValueFactory = _ => 5081
        };
        var allowArbitrary = new Option<bool>("--allow-arbitrary-urls")
        { Description = "Drop the URL allowlist. Only safe in sandboxed contexts." };
        var noEnvAllowlist = new Option<bool>("--no-env-allowlist")
        { Description = "Skip seeding the allowlist from ~/.bowire/environments.json." };

        var serve = new Command("serve", "Run Bowire as an MCP server (AI-agent bridge).");
        serve.Add(bind); serve.Add(port); serve.Add(allowArbitrary); serve.Add(noEnvAllowlist);
        serve.SetAction(async (pr, _) => await McpServeCommand.RunAsync(
            bind: pr.GetValue(bind) ?? "stdio",
            port: pr.GetValue(port),
            allowArbitraryUrls: pr.GetValue(allowArbitrary),
            noEnvAllowlist: pr.GetValue(noEnvAllowlist)).ConfigureAwait(false));

        var mcp = new Command("mcp", "Expose Bowire as an MCP server for AI agents.");
        mcp.Add(serve);
        return mcp;
    }

    // -------------------- plugin --------------------

    private static Command BuildPluginCommand(IConfiguration cfg, string pluginDir)
    {
        var versionOpt = new Option<string?>("--version")
        {
            Description = "Pin to a specific version.",
            DefaultValueFactory = _ => cfg["Bowire:Plugin:Version"]
        };
        var sourcesOpt = new Option<string[]>("--source", "-s")
        { Description = "Custom NuGet feed URL. Repeatable." };
        var fileOpt = new Option<string?>("--file")
        {
            Description = "Install from a local .nupkg instead of a feed.",
            DefaultValueFactory = _ => cfg["Bowire:Plugin:File"]
        };
        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Output directory for downloaded packages.",
            DefaultValueFactory = _ => cfg["Bowire:Plugin:OutputDir"]
        };
        var verboseOpt = new Option<bool>("-v", "--verbose")
        {
            Description = "Verbose output.",
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Plugin:Verbose")
        };
        var packageIdArg = new Argument<string>("packageId") { Description = "NuGet package id." };

        var install = new Command("install", "Install a protocol plugin from NuGet (or --file).");
        install.Add(packageIdArg); install.Add(versionOpt); install.Add(sourcesOpt); install.Add(fileOpt);
        install.SetAction(async (pr, _) =>
        {
            var file = pr.GetValue(fileOpt);
            var sources = pr.GetValue(sourcesOpt) ?? [];
            return !string.IsNullOrEmpty(file)
                ? await PluginManager.InstallFromFileAsync(file, pluginDir, sources).ConfigureAwait(false)
                : await PluginManager.InstallAsync(
                    pr.GetValue(packageIdArg) ?? "", pr.GetValue(versionOpt), pluginDir, sources).ConfigureAwait(false);
        });

        var download = new Command("download", "Download a plugin + its transitive deps as offline .nupkg files.");
        download.Add(packageIdArg); download.Add(versionOpt); download.Add(sourcesOpt); download.Add(outputOpt);
        download.SetAction(async (pr, _) =>
            await PluginManager.DownloadAsync(
                pr.GetValue(packageIdArg) ?? "",
                pr.GetValue(versionOpt),
                pr.GetValue(outputOpt) ?? Directory.GetCurrentDirectory(),
                pr.GetValue(sourcesOpt) ?? []).ConfigureAwait(false));

        var list = new Command("list", "List installed plugins.");
        list.Add(verboseOpt);
        list.SetAction((pr, _) => Task.FromResult(PluginManager.List(pluginDir, pr.GetValue(verboseOpt))));

        var uninstall = new Command("uninstall", "Remove an installed plugin.");
        uninstall.Add(packageIdArg);
        uninstall.SetAction((pr, _) => Task.FromResult(PluginManager.Uninstall(pr.GetValue(packageIdArg) ?? "", pluginDir)));

        var updateIdArg = new Argument<string>("packageId")
        { Description = "Plugin id; omit to update all.", DefaultValueFactory = _ => "" };
        var update = new Command("update", "Update one plugin (or all if no id given).");
        update.Add(updateIdArg); update.Add(versionOpt); update.Add(sourcesOpt);
        update.SetAction(async (pr, _) =>
        {
            var id = pr.GetValue(updateIdArg) ?? "";
            var sources = pr.GetValue(sourcesOpt) ?? [];
            return string.IsNullOrEmpty(id)
                ? await PluginManager.UpdateAllAsync(pluginDir, sources).ConfigureAwait(false)
                : await PluginManager.UpdateAsync(id, pr.GetValue(versionOpt), pluginDir, sources).ConfigureAwait(false);
        });

        var inspect = new Command("inspect", "Inspect a plugin's metadata + protocol contributions.");
        inspect.Add(packageIdArg);
        inspect.SetAction((pr, _) => Task.FromResult(PluginManager.Inspect(pr.GetValue(packageIdArg) ?? "", pluginDir)));

        var plugin = new Command("plugin", "Manage protocol plugins.");
        plugin.Add(install); plugin.Add(download); plugin.Add(list);
        plugin.Add(uninstall); plugin.Add(update); plugin.Add(inspect);
        return plugin;
    }

    // -------------------- test --------------------

    private static Command BuildTestCommand(IConfiguration cfg)
    {
        var collectionPath = new Argument<string?>("recording")
        { Description = "Path to a recording JSON.", DefaultValueFactory = _ => cfg["Bowire:Test:CollectionPath"] };
        var url = new Option<string?>("--url")
        {
            Description = "Override the recording's serverUrl.",
            DefaultValueFactory = _ => cfg["Bowire:Test:Url"]
        };
        var report = new Option<string?>("--report")
        {
            Description = "Write an HTML report to this path.",
            DefaultValueFactory = _ => cfg["Bowire:Test:ReportPath"]
        };
        var junit = new Option<string?>("--junit")
        {
            Description = "Write a JUnit XML report to this path.",
            DefaultValueFactory = _ => cfg["Bowire:Test:JUnitPath"]
        };

        var cmd = new Command("test", "Replay a recording as an assertion-based test suite.");
        cmd.Add(collectionPath); cmd.Add(url); cmd.Add(report); cmd.Add(junit);
        cmd.SetAction(async (pr, _) =>
        {
            var options = new TestCliOptions
            {
                CollectionPath = pr.GetValue(collectionPath),
                ReportPath = pr.GetValue(report),
                JUnitPath = pr.GetValue(junit)
            };
            return await TestRunner.RunAsync(options).ConfigureAwait(false);
        });
        return cmd;
    }
}
