// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.CommandLine.Parsing;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Cli;
using Kuestenlogik.Bowire.Mcp;
using Kuestenlogik.Bowire.Mock.Chaos;
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
    public static async Task<int> RunAsync(string[] args, IConfiguration cfg, string pluginDir,
        TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var root = BuildRoot(args, cfg, pluginDir);
        var invocationConfig = new InvocationConfiguration
        {
            Output = stdout ?? Console.Out,
            Error = stderr ?? Console.Error,
        };

        // #38 — pretty-print parse failures ourselves instead of letting
        // the default ParseErrorAction dump the raw message + full help to
        // stdout. Only the human-error path is intercepted; help / version
        // / the [suggest] completion directive leave Errors empty and flow
        // through InvokeAsync untouched. Colour is enabled only when we own
        // the real console (no custom writer) and it isn't redirected, so
        // piped output and captured test streams stay ANSI-free.
        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            var useColor = stderr is null && !Console.IsErrorRedirected;
            return CliErrorRenderer.Render(parseResult, invocationConfig.Error, useColor);
        }

        return await parseResult.InvokeAsync(invocationConfig).ConfigureAwait(false);
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
        root.Add(new Option<int?>("--port") { Description = "Browser UI port. Default 5080." }.WithPortValidation());
        root.Add(new Option<string>("--title") { Description = "Browser tab title. Default \"Bowire\"." });
        root.Add(new Option<string>("--description") { Description = "Subtitle shown below the title." });
        root.Add(new Option<bool>("--no-browser") { Description = "Don't auto-open a browser window." });
        root.Add(new Option<bool>("--enable-mcp-adapter") { Description = "Expose discovered services as MCP tools." });
        root.Add(new Option<bool>("--update-check") { Description = "Opt in to the daily plugin-update check (off by default). Bowire queries nuget.org once per day for newer versions of every installed sibling plugin and surfaces a count badge in the sidebar. Equivalent to Bowire:PluginUpdateCheck:Enabled=true." });
        root.Add(new Option<string>("--auth-provider") { Description = "Id of the IBowireAuthProvider plugin that should gate workbench access (e.g. 'oidc'). When unset, the workbench stays open — same as today's laptop default. When set, the named plugin must be installed (e.g. `bowire plugin install Kuestenlogik.Bowire.Auth.Oidc`) or Bowire fails fast. Provider-namespaced flags are read from Bowire:Auth:<id>:* — for OIDC: --auth-oidc-authority, --auth-oidc-client-id, etc. (forwarded as Bowire:Auth:Oidc:Authority / ClientId)." });
        root.Add(new Option<bool>("--lock-server-url") { Description = "Disable URL editing in the UI." });
        root.Add(new Option<bool>("--telemetry") { Description = "Enable OpenTelemetry self-observability (#29). Bowire emits traces + Bowire-domain metrics (bowire.invoke.count / duration / bowire.discover.count / bowire.plugin.load / bowire.mock.requests) through the canonical 'Kuestenlogik.Bowire' Meter and ActivitySource. Wire endpoint, headers, and protocol come from the standard OTEL_EXPORTER_OTLP_* env vars. Off by default — laptop installs stay quiet." });
        root.Add(new Option<bool>("--telemetry-strip-method-labels") { Description = "When --telemetry is on, drop the high-cardinality service + method dimensions from emitted metrics. Shared multi-tenant installs (GDPR / HIPAA / SOX) almost always want this; private-network deploys usually leave it off so per-method breakdowns survive." });
        root.Add(new Option<string>("--ai-provider") { Description = "AI provider id for the workbench's chat / hint surface (#25 Phase 2). Default 'ollama' speaks the local Ollama HTTP API on 127.0.0.1:11434; LM Studio on :1234 works through the same client because the wire shape matches. Cloud connectors slot in via the same Microsoft.Extensions.AI IChatClient seam in Phase 3. Maps to Bowire:Ai:ProviderId." });
        root.Add(new Option<string>("--ai-endpoint") { Description = "Override the AI provider's HTTP endpoint. Default 'http://localhost:11434' (Ollama). Set to 'http://localhost:1234' for LM Studio, or to a remote Ollama gateway when the model server runs on another host. Maps to Bowire:Ai:Endpoint." });
        root.Add(new Option<string>("--ai-model") { Description = "Model name passed to the AI provider. Default 'llama3.2:3b' (Ollama's small + free model that runs on a laptop). Use `ollama pull <name>` first; LM Studio uses whatever model is currently loaded. Maps to Bowire:Ai:Model." });
        root.Add(new Option<string>("--plugin-dir") { Description = "Override the plugin directory." });
        var mapBasemapOpt = new Option<string>("--map-basemap")
        {
            Description = "Map widget basemap: 'osm' / 'satellite' / 'demotiles' / 'none', a tile URL with {z}/{x}/{y}, or a style.json URL. Default: 'demotiles'.",
        };
        // Completion sugar (#38): offer the four keyword basemaps to
        // dotnet-suggest without restricting the value — a tile / style URL
        // is still accepted, it just isn't a completion candidate.
        mapBasemapOpt.CompletionSources.Add("osm", "satellite", "demotiles", "none");
        root.Add(mapBasemapOpt);
        root.Add(new Option<string[]>("--disable-plugin")
        {
            Description = "Skip a protocol plugin at startup. Repeat or comma-separate ('--disable-plugin grpc --disable-plugin signalr' or '--disable-plugin grpc,signalr'). Useful when a plugin DLL won't load or its discovery probe is too slow for the current host.",
            AllowMultipleArgumentsPerToken = true,
        });
        var disableCliCommandOpt = new Option<string[]>("--disable-cli-command")
        {
            Description = "Skip an auto-discovered CLI subcommand at startup. Mirrors --disable-plugin but for IBowireCliCommand contributions (scan / future plugin commands). Repeat or comma-separate.",
            AllowMultipleArgumentsPerToken = true,
        };
        root.Add(disableCliCommandOpt);

        root.SetAction(async (pr, ct) =>
            await BrowserUiHost.RunAsync(originalArgs, cfg, pluginDir,
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct).ConfigureAwait(false));

        root.Add(BuildListCommand(cfg));
        root.Add(BuildDescribeCommand(cfg));
        root.Add(BuildCallCommand(cfg));
        root.Add(BuildMockCommand(cfg));
        root.Add(BuildMcpCommand());
        root.Add(BuildPluginCommand(cfg, pluginDir));
        root.Add(BuildTestCommand(cfg));
        root.Add(BuildImportCommand());
        root.Add(BuildJwtCommand());
        root.Add(BuildFuzzCommand());
        root.Add(BuildProxyCommand());
        root.Add(BuildInterceptorCommand());
        root.Add(ExportCommand.Build());
        root.Add(WorkspaceCommand.Build());
        root.Add(RecordingCommand.Build());

        // Auto-discovered CLI commands — scanner today, fuzz / proxy /
        // jwt will follow as they extract out of Tool into their own
        // projects. Same assembly-scan pattern as BowireProtocolRegistry.
        // Disabled-ids parsed straight off the args because System.CommandLine
        // doesn't surface option values before Parse(args), and we need the
        // list at command-build time.
        //
        // Force-load the Scanner assembly so its IBowireCliCommand types
        // are visible to the assembly scan below. A bare typeof() ref
        // can be optimised away by the JIT/Linker, so we instantiate the
        // type — keeps the reference live, and the assembly lands in
        // AppDomain.GetAssemblies() before Discover() walks it.
        try { _ = Activator.CreateInstance<Kuestenlogik.Bowire.Security.Scanner.Cli.ScanCliCommand>(); }
        catch { /* discovery loop below surfaces the real error */ }

        var disabledCli = PreparseRepeatableArg(originalArgs, "--disable-cli-command");
        foreach (var cmd in BowireCliCommandRegistry.Discover(disabledCli).Commands)
        {
            root.Add(cmd.Build());
        }

        return root;
    }

    /// <summary>
    /// Pull repeatable option values out of the raw args array before
    /// System.CommandLine.Parse runs. Needed for options that have to
    /// influence root-command shape (like --disable-cli-command, which
    /// gates which subcommands attach at all). Supports both spaced
    /// (<c>--flag value</c>) and equals (<c>--flag=value</c>) forms,
    /// plus comma-separated values inside one token.
    /// </summary>
    private static List<string> PreparseRepeatableArg(string[] args, string optionName)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? value = null;
            if (string.Equals(a, optionName, StringComparison.Ordinal)
                && i + 1 < args.Length)
            {
                value = args[++i];
            }
            else if (a.StartsWith(optionName + "=", StringComparison.Ordinal))
            {
                value = a[(optionName.Length + 1)..];
            }
            if (value is not null)
            {
                foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    result.Add(part);
                }
            }
        }
        return result;
    }

    // -------------------- per-option validators (#38) --------------------
    // Validators run at Parse time, so a bad --port / --recording / --chaos
    // is rejected before any subcommand action boots a server or opens a
    // socket ("parsed ahead of dispatch"). Every validator skips implicit
    // results (result.Implicit) so a value supplied purely by a config
    // DefaultValueFactory — never typed by the user — is never punished.

    private const int MinPort = 1;
    private const int MaxPort = 65535;

    /// <summary>Reject a TCP port outside 1..65535 on an <see cref="Option{Int32}"/>.</summary>
    private static Option<int> WithPortValidation(this Option<int> opt)
    {
        opt.Validators.Add(result =>
        {
            if (result.Implicit) return;
            CheckPort(result, opt.Name, result.GetValueOrDefault<int>());
        });
        return opt;
    }

    /// <summary>Reject a TCP port outside 1..65535 on a nullable <see cref="Option{T}"/>.</summary>
    private static Option<int?> WithPortValidation(this Option<int?> opt)
    {
        opt.Validators.Add(result =>
        {
            if (result.Implicit) return;
            if (result.GetValueOrDefault<int?>() is int port)
                CheckPort(result, opt.Name, port);
        });
        return opt;
    }

    private static void CheckPort(OptionResult result, string name, int port)
    {
        if (port < MinPort || port > MaxPort)
            result.AddError($"{name}: port must be between {MinPort} and {MaxPort} (got {port}).");
    }

    /// <summary>Reject a <c>--recording</c>-style path that doesn't point at an existing file.</summary>
    private static Option<string?> WithExistingFileValidation(this Option<string?> opt)
    {
        opt.Validators.Add(result =>
        {
            if (result.Implicit) return;
            var path = result.GetValueOrDefault<string?>();
            if (!string.IsNullOrEmpty(path) && !File.Exists(path))
                result.AddError($"{opt.Name}: file not found: '{path}'.");
        });
        return opt;
    }

    /// <summary>Parse a <c>--chaos</c> spec eagerly so a malformed spec fails at Parse time, not mid-boot.</summary>
    private static Option<string?> WithChaosValidation(this Option<string?> opt)
    {
        opt.Validators.Add(result =>
        {
            if (result.Implicit) return;
            var spec = result.GetValueOrDefault<string?>();
            if (string.IsNullOrEmpty(spec)) return;
            try { ChaosOptions.Parse(spec); }
            catch (FormatException ex) { result.AddError(ex.Message); }
        });
        return opt;
    }

    // -------------------- proxy --------------------

    private static Command BuildProxyCommand()
    {
        var proxy = new Command("proxy",
            "Intercepting HTTP/HTTPS proxy. Tier-3 anchor of the security-testing lane (see docs/architecture/security-testing.md).");

        var portOpt = new Option<int>("--port") { Description = "Port the proxy listens on (point browser / client at it). Default 8888." }.WithPortValidation();
        var apiPortOpt = new Option<int>("--api-port") { Description = "Sidecar API port the workbench's Proxy tab reads captured flows from. Default 8889." }.WithPortValidation();
        var capacityOpt = new Option<int>("--capacity") { Description = "Maximum number of flows retained in memory (FIFO eviction). Default 1000." };
        var noMitmOpt = new Option<bool>("--no-mitm") { Description = "Disable HTTPS interception — CONNECT requests are tunneled-and-rejected with 501. Default: MITM enabled." };
        var caDirOpt = new Option<string?>("--ca-dir") { Description = "Override the CA storage directory. Default: ~/.bowire (PFX + DER cert persisted there)." };
        var exportCaOpt = new Option<string?>("--export-ca") { Description = "Copy the public CA certificate to this path (.crt) and exit. Install the file into your trust store to suppress client-side warnings during HTTPS interception." };

        proxy.Add(portOpt);
        proxy.Add(apiPortOpt);
        proxy.Add(capacityOpt);
        proxy.Add(noMitmOpt);
        proxy.Add(caDirOpt);
        proxy.Add(exportCaOpt);

        proxy.SetAction(async (pr, ct) =>
        {
            var options = new ProxyCommand.ProxyOptions
            {
                Port = pr.GetValue(portOpt) is int p and > 0 ? p : 8888,
                ApiPort = pr.GetValue(apiPortOpt) is int ap and > 0 ? ap : 8889,
                Capacity = pr.GetValue(capacityOpt) is int c and > 0 ? c : 1000,
                MitmHttps = !pr.GetValue(noMitmOpt),
                CaDir = pr.GetValue(caDirOpt),
                ExportCa = pr.GetValue(exportCaOpt),
            };
            return await ProxyCommand.RunAsync(options,
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct).ConfigureAwait(false);
        });

        return proxy;
    }

    // -------------------- interceptor --------------------

    private static Command BuildInterceptorCommand()
    {
        var interceptor = new Command("interceptor",
            "Standalone reverse-proxy mode (#307 — Phase C of #153). Fronts an upstream service: clients point at Bowire's listener, every request is forwarded upstream and captured into the same InterceptedFlowStore the embedded middleware (UseBowireInterceptor) uses. The workbench's 'Intercepted' rail reads the sidecar API surface this command exposes.");

        var upstreamOpt = new Option<string>("--upstream")
        {
            Description = "Upstream service URL the listener forwards to (e.g. https://api.example.com). Required.",
            Required = true,
        };
        var listenOpt = new Option<string>("--listen")
        {
            Description = "host:port the edge listener binds to (e.g. 127.0.0.1:8080, 0.0.0.0:9000, :8080). Default 127.0.0.1:0 (loopback + ephemeral port).",
        };
        var apiPortOpt = new Option<int?>("--api-port") { Description = "Sidecar API port the workbench's Intercepted rail reads from. Default 5089." }.WithPortValidation();
        var capacityOpt = new Option<int?>("--capacity") { Description = "Maximum number of flows retained in memory (FIFO eviction). Default 1000." };
        var maxBodyOpt = new Option<int?>("--max-body-bytes") { Description = "Per-side body capture cap. Default 1048576 (1 MiB)." };
        var allowSelfSignedOpt = new Option<bool>("--allow-self-signed-upstream")
        {
            Description = "Accept the upstream's TLS cert without chain validation. Useful when fronting a dev-mode service with a self-signed cert. Off by default."
        };
        var tlsOpt = new Option<bool>("--tls")
        {
            Description = "Serve HTTPS on the edge listener using a leaf certificate minted from Bowire's MITM CA (reuses #36's CA flow). Install the CA into the client trust store via `bowire proxy --export-ca` to avoid handshake warnings."
        };
        var tlsHostOpt = new Option<string?>("--tls-host")
        {
            Description = "Hostname the minted leaf certificate is issued for (SAN). Default: the listen address. Set when clients connect via a custom DNS name (e.g. api.local pointing to 127.0.0.1)."
        };
        var caDirOpt = new Option<string?>("--ca-dir")
        {
            Description = "Override the CA storage directory (default: ~/.bowire). Same flag as `bowire proxy`."
        };

        interceptor.Add(upstreamOpt);
        interceptor.Add(listenOpt);
        interceptor.Add(apiPortOpt);
        interceptor.Add(capacityOpt);
        interceptor.Add(maxBodyOpt);
        interceptor.Add(allowSelfSignedOpt);
        interceptor.Add(tlsOpt);
        interceptor.Add(tlsHostOpt);
        interceptor.Add(caDirOpt);

        interceptor.SetAction(async (pr, ct) =>
        {
            var options = new InterceptorCommand.InterceptorOptions
            {
                Upstream = pr.GetValue(upstreamOpt) ?? "",
                Listen = string.IsNullOrEmpty(pr.GetValue(listenOpt)) ? "127.0.0.1:0" : pr.GetValue(listenOpt)!,
                ApiPort = pr.GetValue(apiPortOpt) ?? 5089,
                Capacity = pr.GetValue(capacityOpt) is int c and > 0 ? c : 1000,
                MaxBodyBytes = pr.GetValue(maxBodyOpt) is int mb and > 0 ? mb : 1024 * 1024,
                AllowSelfSignedUpstream = pr.GetValue(allowSelfSignedOpt),
                Tls = pr.GetValue(tlsOpt),
                TlsHost = pr.GetValue(tlsHostOpt),
                CaDir = pr.GetValue(caDirOpt),
            };
            return await InterceptorCommand.RunAsync(options,
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct).ConfigureAwait(false);
        });

        return interceptor;
    }

    // -------------------- fuzz --------------------

    // internal so the #38 completion-source wiring on --payloads can be
    // asserted from Kuestenlogik.Bowire.Tests without booting a fuzz run.
    internal static Command BuildFuzzCommand()
    {
        var fuzz = new Command("fuzz",
            "Schema-aware fuzzing of a single field. Tier-2 anchor of the security-testing lane.");

        var targetOpt = new Option<string>("--target") { Description = "Target base URL.", Required = true };
        var templateOpt = new Option<string>("--template") { Description = "Recording-style JSON file describing the request shape (httpVerb / httpPath / body).", Required = true };
        var fieldOpt = new Option<string>("--field") { Description = "JSONPath into the request body identifying the field to fuzz (e.g. $.username or $.filter.id).", Required = true };
        var categoryOpt = new Option<string>("--payloads") { Description = "Payload category: sqli / xss / pathtrav / cmdinj.", Required = true };
        categoryOpt.CompletionSources.Add("sqli", "xss", "pathtrav", "cmdinj");
        var forceOpt = new Option<bool>("--force") { Description = "Run even when the field's value-shape doesn't match the payload class (e.g. fuzz a numeric field with string payloads anyway)." };
        var timeoutOpt = new Option<int>("--timeout") { Description = "Per-payload HTTP timeout in seconds. Default 30." };
        var allowSelfSignedOpt = new Option<bool>("--allow-self-signed-certs") { Description = "Accept self-signed certs on the target." };
        var authHeaderOpt = new Option<string[]>("--auth-header")
        {
            Description = "Add an HTTP header to every fuzz request (typically Authorization: Bearer …). Repeatable.",
            AllowMultipleArgumentsPerToken = false,
        };

        fuzz.Add(targetOpt);
        fuzz.Add(templateOpt);
        fuzz.Add(fieldOpt);
        fuzz.Add(categoryOpt);
        fuzz.Add(forceOpt);
        fuzz.Add(timeoutOpt);
        fuzz.Add(allowSelfSignedOpt);
        fuzz.Add(authHeaderOpt);

        fuzz.SetAction(async (pr, ct) =>
        {
            var options = new FuzzOptions
            {
                Target = pr.GetValue(targetOpt) ?? "",
                Template = pr.GetValue(templateOpt),
                Field = pr.GetValue(fieldOpt),
                Category = pr.GetValue(categoryOpt),
                Force = pr.GetValue(forceOpt),
                TimeoutSeconds = pr.GetValue(timeoutOpt) is int t and > 0 ? t : 30,
                AllowSelfSignedCerts = pr.GetValue(allowSelfSignedOpt),
                AuthHeaders = pr.GetValue(authHeaderOpt) ?? Array.Empty<string>(),
            };
            return await FuzzCommand.RunAsync(options,
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct).ConfigureAwait(false);
        });

        return fuzz;
    }

    // -------------------- jwt --------------------

    private static Command BuildJwtCommand()
    {
        var jwt = new Command("jwt",
            "JWT decode / tamper toolkit. Tier-2 anchor of the security-testing lane (see docs/architecture/security-testing.md).");

        var tokenArg = new Argument<string>("token") { Description = "The JWT to operate on (header.payload.signature)." };

        var decode = new Command("decode", "Decode a JWT and pretty-print the header + payload.");
        decode.Add(tokenArg);
        decode.SetAction(async (pr, ct) =>
            await JwtCommand.RunDecodeAsync(pr.GetValue(tokenArg) ?? "",
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct).ConfigureAwait(false));

        var tamper = new Command("tamper", "Produce a tampered JWT (alg:none downgrade, claim mutation, optional HS256 re-signing).");
        var algNoneOpt = new Option<bool>("--alg-none") { Description = "Downgrade the token to alg:none (drops the signature; the classic JWT bypass)." };
        var setOpt = new Option<string[]>("--set")
        {
            Description = "Mutate a payload claim: --set claim=value. Repeatable. Values parsed as JSON literals first (so --set exp=9999999999 lands as number) and fall back to string.",
            AllowMultipleArgumentsPerToken = true,
        };
        var secretOpt = new Option<string>("--secret") { Description = "Re-sign with HMAC-SHA256 using this secret (overrides --alg-none). Useful when probing weak-secret-based JWT validation." };
        tamper.Add(tokenArg);
        tamper.Add(algNoneOpt);
        tamper.Add(setOpt);
        tamper.Add(secretOpt);
        tamper.SetAction(async (pr, ct) =>
            await JwtCommand.RunTamperAsync(
                pr.GetValue(tokenArg) ?? "",
                pr.GetValue(algNoneOpt),
                pr.GetValue(setOpt) ?? Array.Empty<string>(),
                pr.GetValue(secretOpt),
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct).ConfigureAwait(false));

        jwt.Add(decode);
        jwt.Add(tamper);
        return jwt;
    }

    // scan: contributed via IBowireCliCommand from
    // Kuestenlogik.Bowire.Security.Scanner — picked up by the auto-
    // discovery loop above. See ScanCliCommand.Build().

    // -------------------- import --------------------

    private static Command BuildImportCommand()
    {
        var fileArg = new Argument<string>("file") { Description = "Path to the HAR document." };
        var outOpt = new Option<string?>("--out", "-o")
        {
            Description = "Output path for the recording. Use \"-\" to stream to stdout. " +
                          "Default: <har-basename>.bwr next to the input file."
        };
        var nameOpt = new Option<string?>("--name", "-n")
        { Description = "Recording name. Defaults to the HAR creator name or \"Imported HAR\"." };

        var har = new Command("har", "Convert a HAR 1.2 document into a .bwr recording.");
        har.Add(fileArg); har.Add(outOpt); har.Add(nameOpt);
        har.SetAction(async (pr, _) =>
        {
            var input = pr.GetValue(fileArg) ?? "";
            var output = pr.GetValue(outOpt);
            var name = pr.GetValue(nameOpt);
            // Sensible default: drop the .bwr next to the HAR with the same
            // basename. Stays predictable regardless of cwd.
            if (string.IsNullOrEmpty(output))
            {
                var basename = Path.GetFileNameWithoutExtension(input);
                var dir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? "";
                output = Path.Combine(dir, basename + ".bwr");
            }
            return await HarImporter.ImportAsync(input, output, name,
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false);
        });

        var import = new Command("import", "Convert external trace formats into Bowire recordings.");
        import.Add(har);
        return import;
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
            await CliHandler.ListAsync(BuildCliOptions(pr, url, plaintext, verbose, new Option<bool>("--compact"), null, null, null),
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false));
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
                pr, url, plaintext, verbose, new Option<bool>("--compact"), null, null, pr.GetValue(target)),
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false));
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
                pr, url, plaintext, verbose, compact, data, headers, pr.GetValue(target)),
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false));
        return cmd;
    }

    // -------------------- mock --------------------

    // internal so the tests in Kuestenlogik.Bowire.Tests can exercise
    // the System.CommandLine wiring (the #211 positional shape +
    // mutex against --recording / --schema) without booting a real
    // mock server.
    internal static Command BuildMockCommand(IConfiguration cfg)
    {
        var recording = new Option<string?>("--recording", "-r")
        {
            Description = "Path to a Bowire recording JSON.",
            DefaultValueFactory = _ => cfg["Bowire:Mock:RecordingPath"]
        }.WithExistingFileValidation();
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
        }.WithPortValidation();
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
        }.WithChaosValidation();
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

        // #211 — positional shape `bowire mock foo.bwr`. The option form
        // (--recording / -r) stays for back-compat + script flexibility;
        // the positional is sugar that converges on the same MockServer
        // path. Mutex against the option form + the schema-mock siblings
        // is enforced in the action body — System.CommandLine can't
        // express a positional-or-option-or-the-other-three constraint
        // declaratively, so we surface the error after Parse with a
        // 64 EX_USAGE exit so a CI step can branch on the failure mode.
        var positionalPath = new Argument<string?>("path")
        {
            Description = "Path to a Bowire .bwr recording. Equivalent to --recording <path>; convenient for one-shot replay. See docs/recordings/bwr-format.md for the file format.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        // Same existence guard as --recording (#38) — the positional
        // collapses onto RecordingPath, so a bad path fails identically
        // at Parse time rather than mid-boot.
        positionalPath.Validators.Add(result =>
        {
            if (result.Implicit) return;
            var path = result.GetValueOrDefault<string?>();
            if (!string.IsNullOrEmpty(path) && !File.Exists(path))
                result.AddError($"path: recording file not found: '{path}'.");
        });

        var cmd = new Command("mock", "Replay a recording (or schema) as a local API endpoint. Pass a .bwr file as the positional argument for the common case, or use --schema / --grpc-schema / --graphql-schema for schema-only mocks.");
        cmd.Add(positionalPath);
        cmd.Add(recording); cmd.Add(schema); cmd.Add(grpcSchema); cmd.Add(graphqlSchema);
        cmd.Add(port); cmd.Add(host); cmd.Add(select); cmd.Add(noWatch);
        cmd.Add(stateful); cmd.Add(statefulOnce); cmd.Add(loop); cmd.Add(autoInstall);
        cmd.Add(chaos); cmd.Add(captureMiss); cmd.Add(controlToken);
        cmd.SetAction(async (pr, ct) =>
        {
            var positional = pr.GetValue(positionalPath);
            var recordingOpt = pr.GetValue(recording);
            var schemaOpt = pr.GetValue(schema);
            var grpcSchemaOpt = pr.GetValue(grpcSchema);
            var graphqlSchemaOpt = pr.GetValue(graphqlSchema);

            // Mutex: the positional collapses to --recording, but only
            // one of (positional, --recording) is allowed, AND the
            // positional can't appear with a schema-mock flag because
            // schema mocks don't take a recording. The existing
            // recording-vs-schema mutex inside MockCommand.RunAsync
            // covers the option-only side; this guard covers the
            // positional-plus-X cases.
            if (positional is not null)
            {
                if (recordingOpt is not null)
                {
                    await pr.InvocationConfiguration.Error
                        .WriteLineAsync("bowire mock: pass the recording EITHER as the positional argument OR via --recording, not both.")
                        .ConfigureAwait(false);
                    return 64;
                }
                if (schemaOpt is not null || grpcSchemaOpt is not null || graphqlSchemaOpt is not null)
                {
                    await pr.InvocationConfiguration.Error
                        .WriteLineAsync("bowire mock: pick one input — a recording file (positional or --recording) OR one of --schema / --grpc-schema / --graphql-schema.")
                        .ConfigureAwait(false);
                    return 64;
                }
                recordingOpt = positional;
            }

            var options = new MockCliOptions
            {
                RecordingPath = recordingOpt,
                SchemaPath = schemaOpt,
                GrpcSchemaPath = grpcSchemaOpt,
                GraphQlSchemaPath = graphqlSchemaOpt,
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
            return await MockCommand.RunAsync(options,
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct).ConfigureAwait(false);
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
        }.WithPortValidation();
        var allowArbitrary = new Option<bool>("--allow-arbitrary-urls")
        { Description = "Drop the URL allowlist. Only safe in sandboxed contexts." };
        var noEnvAllowlist = new Option<bool>("--no-env-allowlist")
        { Description = "Skip seeding the allowlist from ~/.bowire/environments.json." };
        var allowInvoke = new Option<bool>("--allow-invoke")
        { Description = "Widen the allowlist to every URL the user has typed at least once (~/.bowire/typed-urls.json). Strictly additive with the environments seed." };
        var noConfirm = new Option<bool>("--no-confirm")
        { Description = "Skip the two-step pending-confirmation gate on mutator tools (bowire.mock.start, bowire.record.start). Use when the agent host already enforces approvals." };
        var attach = new Option<string?>("--attach")
        { Description = "Forwarder mode (#286): relay every incoming MCP request to a parent Bowire MCP endpoint. Accepts host:port shorthand (expanded to http://host:port/bowire/mcp) or an absolute http(s) URI. Mutually exclusive in spirit with --allow-arbitrary-urls / --allow-invoke / --no-confirm — those configure the *local* tool registry, which forwarder mode skips entirely." };
        var attachToken = new Option<string?>("--attach-token")
        { Description = "Bearer token attached to every outbound request to the parent (Authorization: Bearer <token>). Required when the parent was started with --token <secret>." };
        var token = new Option<string?>("--token")
        { Description = "Require Authorization: Bearer <secret> on every inbound /bowire/mcp request (--bind http only). Pair with --attach-token on the child." };

        var serve = new Command("serve", "Run Bowire as an MCP server (AI-agent bridge).");
        serve.Add(bind); serve.Add(port); serve.Add(allowArbitrary); serve.Add(noEnvAllowlist);
        serve.Add(allowInvoke); serve.Add(noConfirm);
        serve.Add(attach); serve.Add(attachToken); serve.Add(token);
        serve.SetAction(async (pr, ct) => await McpServeCommand.RunAsync(
            bind: pr.GetValue(bind) ?? "stdio",
            port: pr.GetValue(port),
            allowArbitraryUrls: pr.GetValue(allowArbitrary),
            noEnvAllowlist: pr.GetValue(noEnvAllowlist),
            allowInvoke: pr.GetValue(allowInvoke),
            noConfirm: pr.GetValue(noConfirm),
            stdout: pr.InvocationConfiguration.Output,
            stderr: pr.InvocationConfiguration.Error,
            ct: ct,
            attach: pr.GetValue(attach),
            attachToken: pr.GetValue(attachToken),
            token: pr.GetValue(token)).ConfigureAwait(false));

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
            Description = "Install from a local .nupkg, or a sidecar .zip (local path, http(s):// URL, or oci:// registry ref), instead of a feed.",
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
        // Install accepts a packageId OR a --file path (sidecar zip /
        // oci ref / .nupkg). The shared packageIdArg above is required
        // for the download / uninstall / inspect commands; install gets
        // its own optional variant so users can write
        // `bowire plugin install --file my-sidecar.zip` without
        // inventing a dummy package id the action would just ignore.
        var installPackageIdArg = new Argument<string>("packageId")
        {
            Description = "NuGet package id. Optional when --file points to a sidecar .zip or an oci:// reference; required when resolving from a feed.",
            DefaultValueFactory = _ => string.Empty,
        };
        var prereleaseOpt = new Option<bool>("--prerelease")
        {
            Description = "Allow pre-release versions (1.0.0-rc.1, &c) when resolving the latest. Matches `dotnet add package --prerelease`. Ignored when --version pins an exact version.",
        };

        var install = new Command("install", "Install a protocol plugin from NuGet, a local .nupkg, or a sidecar .zip.");
        install.Add(installPackageIdArg); install.Add(versionOpt); install.Add(sourcesOpt); install.Add(fileOpt); install.Add(prereleaseOpt);
        install.SetAction(async (pr, _) =>
        {
            var file = pr.GetValue(fileOpt);
            var sources = pr.GetValue(sourcesOpt) ?? [];
            var prerelease = pr.GetValue(prereleaseOpt);
            if (!string.IsNullOrEmpty(file))
            {
                // A .zip (local/http) or an oci:// ref is a sidecar
                // (any-language) plugin; a .nupkg is a .NET plugin. Route
                // on the shape so one flag covers every install kind.
                var isSidecar = file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    || file.StartsWith("oci://", StringComparison.OrdinalIgnoreCase);
                var io = pr.InvocationConfiguration;
                return isSidecar
                    ? await PluginManager.InstallSidecarFromZipAsync(file, pluginDir, io.Output, io.Error).ConfigureAwait(false)
                    : await PluginManager.InstallFromFileAsync(file, pluginDir, sources, io.Output, io.Error).ConfigureAwait(false);
            }
            var pkgId = pr.GetValue(installPackageIdArg) ?? "";
            var ioCfg = pr.InvocationConfiguration;
            if (string.IsNullOrWhiteSpace(pkgId))
            {
                await ioCfg.Output.WriteLineAsync("  Usage: bowire plugin install <packageId> [--version <ver>] [--source <url>...]").ConfigureAwait(false);
                await ioCfg.Output.WriteLineAsync("         bowire plugin install --file <sidecar.zip|oci://...|local.nupkg>").ConfigureAwait(false);
                return 2;
            }
            return await PluginManager.InstallAsync(
                pkgId, pr.GetValue(versionOpt), pluginDir, sources,
                includePrerelease: prerelease,
                stdout: ioCfg.Output, stderr: ioCfg.Error).ConfigureAwait(false);
        });

        var download = new Command("download", "Download a plugin + its transitive deps as offline .nupkg files.");
        download.Add(packageIdArg); download.Add(versionOpt); download.Add(sourcesOpt); download.Add(outputOpt);
        download.SetAction(async (pr, _) =>
            await PluginManager.DownloadAsync(
                pr.GetValue(packageIdArg) ?? "",
                pr.GetValue(versionOpt),
                pr.GetValue(outputOpt) ?? Directory.GetCurrentDirectory(),
                pr.GetValue(sourcesOpt) ?? [],
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false));

        var list = new Command("list", "List installed plugins.");
        list.Add(verboseOpt);
        list.SetAction((pr, _) => Task.FromResult(PluginManager.List(
            pluginDir, pr.GetValue(verboseOpt),
            pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error)));

        var uninstall = new Command("uninstall", "Remove an installed plugin.");
        uninstall.Add(packageIdArg);
        uninstall.SetAction((pr, _) => Task.FromResult(PluginManager.Uninstall(
            pr.GetValue(packageIdArg) ?? "", pluginDir,
            pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error)));

        var updateIdArg = new Argument<string>("packageId")
        { Description = "Plugin id; omit to update all.", DefaultValueFactory = _ => "" };
        var update = new Command("update", "Update one plugin (or all if no id given).");
        update.Add(updateIdArg); update.Add(versionOpt); update.Add(sourcesOpt); update.Add(prereleaseOpt);
        update.SetAction(async (pr, _) =>
        {
            var id = pr.GetValue(updateIdArg) ?? "";
            var sources = pr.GetValue(sourcesOpt) ?? [];
            var prerelease = pr.GetValue(prereleaseOpt);
            var ioCfg = pr.InvocationConfiguration;
            return string.IsNullOrEmpty(id)
                ? await PluginManager.UpdateAllAsync(pluginDir, sources, includePrerelease: prerelease,
                    stdout: ioCfg.Output, stderr: ioCfg.Error).ConfigureAwait(false)
                : await PluginManager.UpdateAsync(id, pr.GetValue(versionOpt), pluginDir, sources,
                    includePrerelease: prerelease,
                    stdout: ioCfg.Output, stderr: ioCfg.Error).ConfigureAwait(false);
        });

        var inspect = new Command("inspect", "Inspect a plugin's metadata + protocol contributions.");
        inspect.Add(packageIdArg);
        inspect.SetAction((pr, _) => Task.FromResult(PluginManager.Inspect(
            pr.GetValue(packageIdArg) ?? "", pluginDir,
            pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error)));

        var plugin = new Command("plugin", "Manage protocol plugins.");
        plugin.Add(install); plugin.Add(download); plugin.Add(list);
        plugin.Add(uninstall); plugin.Add(update); plugin.Add(inspect);
        return plugin;
    }

    // -------------------- test --------------------

    private static Command BuildTestCommand(IConfiguration cfg)
    {
        // v2.2 — accepts EITHER a recording (legacy v2.1 test-collection
        // shape) OR a Flow JSON document (T2 deliverable). The runner
        // sniffs the JSON shape and dispatches to the right backend so
        // operators don't have to pick a flag. Positional arg therefore
        // describes both formats.
        var collectionPath = new Argument<string?>("file")
        { Description = "Path to a recording (v2.1) or Flow JSON file (v2.2). Format is auto-detected by JSON shape.", DefaultValueFactory = _ => cfg["Bowire:Test:CollectionPath"] };
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
        var sarif = new Option<string?>("--sarif")
        {
            Description = "Write a SARIF 2.1.0 report to this path (GitHub Code Scanning, GitLab, Azure DevOps).",
            DefaultValueFactory = _ => cfg["Bowire:Test:SarifPath"]
        };
        var annotations = new Option<bool>("--annotations")
        {
            Description = "Emit GitHub Actions ::error annotations for every failure (inline PR annotations without a reporter action).",
        };
        var updateSnapshots = new Option<bool>("--update-snapshots")
        {
            Description = "Re-capture every snapshot baseline from the actual responses instead of diffing (Flow files only).",
        };
        // v2.2 T2 — Flow-runner specific. Ignored for the recording
        // codepath which already carries serverUrl + environment per
        // test-collection.
        var baseUrl = new Option<string?>("--base-url")
        {
            Description = "Fallback server URL for Flow steps that don't set their own serverUrl. Flow JSON files only; ignored for recordings.",
            DefaultValueFactory = _ => cfg["Bowire:Test:BaseUrl"]
        };
        var env = new Option<string[]>("--env", "--vars")
        {
            Description = "Variable for the Flow {{name}} / ${name} resolver. KEY=VALUE; repeatable. --vars is an alias.",
            AllowMultipleArgumentsPerToken = false,
        };
        var envFile = new Option<string[]>("--env-file")
        {
            Description = "File with one KEY=VALUE per line (dotenv-style; blank lines and # comments ignored) for the Flow resolver. Repeatable; --env repeats win over file entries.",
            AllowMultipleArgumentsPerToken = false,
            DefaultValueFactory = _ => cfg["Bowire:Test:EnvFile"] is { Length: > 0 } p ? [p] : [],
        };

        var cmd = new Command("test", "Run an assertion-based test suite. Accepts a recording JSON (v2.1 test-collection format) or a Flow JSON document (v2.2 — the T2 CI runner). Format auto-detected.");
        cmd.Add(collectionPath); cmd.Add(url); cmd.Add(report); cmd.Add(junit);
        cmd.Add(sarif); cmd.Add(annotations); cmd.Add(updateSnapshots);
        cmd.Add(baseUrl); cmd.Add(env); cmd.Add(envFile);
        cmd.SetAction(async (pr, _) =>
        {
            var options = new TestCliOptions
            {
                CollectionPath = pr.GetValue(collectionPath),
                ReportPath = pr.GetValue(report),
                JUnitPath = pr.GetValue(junit),
                SarifPath = pr.GetValue(sarif),
                Annotations = pr.GetValue(annotations),
                UpdateSnapshots = pr.GetValue(updateSnapshots),
                BaseUrl = pr.GetValue(baseUrl),
                EnvOverrides = pr.GetValue(env) ?? Array.Empty<string>(),
                EnvFiles = pr.GetValue(envFile) ?? Array.Empty<string>(),
            };
            return await TestRunner.RunAsync(
                options,
                pr.InvocationConfiguration.Output,
                pr.InvocationConfiguration.Error).ConfigureAwait(false);
        });
        return cmd;
    }
}
