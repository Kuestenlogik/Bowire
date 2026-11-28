// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.CommandLine.Parsing;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.App.Plugins;
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
    /// <summary>
    /// True when the invocation needs no protocol plugin, so Program.cs skips
    /// the pre-dispatch <see cref="IBowirePluginLoader.Load"/>:
    /// <list type="bullet">
    ///   <item>the <c>plugin</c> management verbs (install / uninstall / update /
    ///   download / list / inspect) act on the plugin <em>directory</em> through
    ///   PluginManager, and eager-loading a plugin assembly memory-maps its DLL,
    ///   so Windows refuses the delete a subsequent uninstall / update needs;</item>
    ///   <item><c>send</c> (#678) sends a request it was given in full, and its
    ///   startup cost is the point of the command.</item>
    /// </list>
    /// </summary>
    public static bool SkipsPluginLoad(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && args[0] is "plugin" or "send";
    }

    /// <summary>
    /// True for <c>send</c>, which also skips the scan for plugin-contributed
    /// subcommands: none of them can be reached from it.
    /// </summary>
    internal static bool IsSendCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "send", StringComparison.Ordinal);

    /// <summary>
    /// How every command line is parsed: without response files (#974).
    /// System.CommandLine would replace each <c>@</c> token with the tokens
    /// of that file, so <c>-d @request.json</c> reached <c>call</c> as the
    /// file split on whitespace with its quotes stripped. Every <c>@</c>
    /// Bowire documents means "read this file", and the command that takes
    /// it reads it. Response files of Bowire's own, <c>@file.rsp</c>, are
    /// expanded before parsing by <see cref="ResponseFiles"/> (#976).
    /// </summary>
    internal static ParserConfiguration Parsing() => new() { ResponseFileTokenReplacer = null };

    /// <summary>
    /// Parse <paramref name="args"/> and run the subcommand they name.
    /// </summary>
    /// <param name="args">The command line, without the executable name.</param>
    /// <param name="cfg">Configuration the subcommands bind their options from.</param>
    /// <param name="plugins">Loader whose packages contribute protocol subcommands.</param>
    /// <param name="stdout">Output sink; defaults to the console.</param>
    /// <param name="stderr">Error sink; defaults to the console.</param>
    /// <param name="cancellationToken">
    /// Stops a long-running subcommand — <c>proxy</c>, <c>mcp serve</c>,
    /// <c>mock</c>. Without it those could be started through this entry
    /// point and never stopped, which is why their CLI wiring had no test
    /// (#637): the only way to exercise the handler was to run it forever.
    /// </param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, IConfiguration cfg, IBowirePluginLoader plugins,
        TextWriter? stdout = null, TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        var root = BuildRoot(args, cfg, plugins);
        // #690 - help headings and descriptions in the same language.
        HelpLocalization.ApplyCulture();
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
        // #818 — provider-namespaced auth flags (--auth-oidc-authority …)
        // belong to a plugin and cannot be declared here; they reach the
        // configuration through AuthProviderFlags, not through the parser.
        var parseResult = root.Parse(Kuestenlogik.Bowire.App.Configuration.AuthProviderFlags.Extract(args).Remaining,
            Parsing());
        if (parseResult.Errors.Count > 0)
        {
            var useColor = stderr is null && !Console.IsErrorRedirected;
            return CliErrorRenderer.Render(parseResult, invocationConfig.Error, useColor);
        }

        // #690 - a help invocation writes through a writer that translates the
        // one heading System.CommandLine leaves in English.
        var output = invocationConfig.Output;
        var helpOutput = HelpLocalization.ForHelp(parseResult, output);
        if (ReferenceEquals(helpOutput, output))
            return await parseResult.InvokeAsync(invocationConfig, cancellationToken).ConfigureAwait(false);
        invocationConfig.Output = helpOutput;
        try
        {
            return await parseResult.InvokeAsync(invocationConfig, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await helpOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static RootCommand BuildRoot(string[] originalArgs, IConfiguration cfg, IBowirePluginLoader plugins)
    {
        // The plugin verbs only need the directory, and their nine action
        // lambdas see a plain string — keeping that shape means the loader
        // reaches the two commands that actually load (browser UI, mock)
        // without churning the installer surface.
        var pluginDir = plugins.Options.PluginDirectory;

        // #549: hand the resolved directory to Core. BowirePluginOptions
        // owns the precedence (--plugin-dir over BOWIRE_PLUGIN_DIR over
        // config file over default) and stays here, because --plugin-dir
        // is a CLI notion; what Core needs is only the answer. Without
        // this the workbench's plugin list, its install shell-out, the
        // update check and sidecar discovery each fell back to the
        // storage default and ignored the directory this process loads
        // from.
        Kuestenlogik.Bowire.Plugins.BowirePluginRoot.Apply(pluginDir);
        var root = new RootCommand(
            BowireLocale.T("cli.bowire-cli.root.command"));

        // Root-level options describe the browser-UI defaults so
        // `bowire --port 6000 --url http://api.local` resolves through
        // the typed parser. The default action delegates to
        // BrowserUiHost which still binds via BowireConfiguration to
        // pick up multi-URL and url-file support natively.
        // #680 — how every outbound connection leaves the machine. Recursive,
        // so `bowire call`, `bowire test`, `bowire mock --proxy` upstreams and
        // the workbench all read them. The values reach BowireNetworkPolicy
        // through BowireConfiguration's switch mappings (Bowire:Network:*).
        root.Add(new Option<string>("--proxy-url")
        {
            Description = BowireLocale.T("cli.bowire-cli.proxy-url"),
            Recursive = true,
        });
        root.Add(new Option<string>("--no-proxy")
        {
            Description = BowireLocale.T("cli.bowire-cli.no-proxy"),
            Recursive = true,
        });
        root.Add(new Option<string>("--ca-bundle")
        {
            Description = BowireLocale.T("cli.bowire-cli.ca-bundle"),
            Recursive = true,
        });
        root.Add(new Option<string[]>("--url")
        {
            Description = BowireLocale.T("cli.bowire-cli.url"),
            AllowMultipleArgumentsPerToken = true
        });
        root.Add(new Option<string>("--url-file") { Description = BowireLocale.T("cli.bowire-cli.url-file") });
        root.Add(new Option<int?>("--port")
        {
            Description = BowireLocale.T("cli.bowire-cli.port"),
        }.WithPortValidation(allowEphemeral: true));
        root.Add(new Option<string>("--port-file")
        {
            Description = BowireLocale.T("cli.bowire-cli.port-file"),
        });
        root.Add(new Option<string>("--title") { Description = BowireLocale.T("cli.bowire-cli.title") });
        root.Add(new Option<string>("--description") { Description = BowireLocale.T("cli.bowire-cli.description") });
        root.Add(new Option<bool>("--no-browser") { Description = BowireLocale.T("cli.bowire-cli.no-browser") });
        root.Add(new Option<bool>("--enable-mcp-adapter") { Description = BowireLocale.T("cli.bowire-cli.enable-mcp-adapter") });
        root.Add(new Option<bool>("--auto-create-initial-workspace") { Description = BowireLocale.T("cli.bowire-cli.auto-create-initial-workspace") });
        root.Add(new Option<bool>("--update-check") { Description = BowireLocale.T("cli.bowire-cli.update-check") });
        root.Add(new Option<string>("--auth-provider") { Description = BowireLocale.T("cli.bowire-cli.auth-provider") });
        root.Add(new Option<bool>("--lock-server-url") { Description = BowireLocale.T("cli.bowire-cli.lock-server-url") });
        root.Add(new Option<string[]>("--allowed-server-url")
        {
            Description = BowireLocale.T("cli.bowire-cli.allowed-server-url"),
            AllowMultipleArgumentsPerToken = true,
        });
        root.Add(new Option<string[]>("--allowed-auth-url")
        {
            Description = BowireLocale.T("cli.bowire-cli.allowed-auth-url"),
            AllowMultipleArgumentsPerToken = true,
        });
        root.Add(new Option<bool>("--telemetry") { Description = BowireLocale.T("cli.bowire-cli.telemetry") });
        root.Add(new Option<bool>("--telemetry-strip-method-labels") { Description = BowireLocale.T("cli.bowire-cli.telemetry-strip-method-labels") });
        root.Add(new Option<string>("--ai-provider") { Description = BowireLocale.T("cli.bowire-cli.ai-provider") });
        root.Add(new Option<string>("--ai-endpoint") { Description = BowireLocale.T("cli.bowire-cli.ai-endpoint") });
        root.Add(new Option<string>("--ai-model") { Description = BowireLocale.T("cli.bowire-cli.ai-model") });
        root.Add(new Option<string>("--oast-server") { Description = BowireLocale.T("cli.bowire-cli.oast-server") });
        root.Add(new Option<string>("--oast-token") { Description = BowireLocale.T("cli.bowire-cli.oast-token") });
        // #537 — catalogue-provider flags. The browser UI's "Browse
        // catalogue" surface is driven entirely by these; without one the
        // workbench keeps its manual-URL-entry behaviour. Values land in
        // Bowire:Discovery:Catalogue:* via the switch mappings in
        // BrowserUiHost.DefaultHostRunner (AddBowireCatalogue binds off the
        // host's own configuration, not off BrowserUiOptions).
        var catalogueProviderOpt = new Option<string>("--catalogue-provider")
        {
            Description = BowireLocale.T("cli.bowire-cli.catalogue-provider"),
        };
        catalogueProviderOpt.CompletionSources.Add("local", "http", "consul", "kubernetes", "agent");
        root.Add(catalogueProviderOpt);
        root.Add(new Option<string>("--catalogue-path") { Description = BowireLocale.T("cli.bowire-cli.catalogue-path") });
        root.Add(new Option<string>("--catalogue-url") { Description = BowireLocale.T("cli.bowire-cli.catalogue-url") });
        root.Add(new Option<string>("--catalogue-consul") { Description = BowireLocale.T("cli.bowire-cli.catalogue-consul") });
        // #818 — recursive: the plugin verbs' usage lines and `plugin --help`
        // advertise `--plugin-dir` (`bowire plugin list --plugin-dir ./x`), and
        // as a root-only option the parser refused it after any subcommand.
        // The value itself is read from the bootstrap configuration, which
        // sees the whole argument list wherever the flag stands.
        root.Add(new Option<string>("--plugin-dir") { Description = BowireLocale.T("cli.bowire-cli.plugin-dir"), Recursive = true });
        var mapBasemapOpt = new Option<string>("--map-basemap")
        {
            Description = BowireLocale.T("cli.bowire-cli.map-basemap"),
        };
        // Completion sugar (#38): offer the four keyword basemaps to
        // dotnet-suggest without restricting the value — a tile / style URL
        // is still accepted, it just isn't a completion candidate.
        mapBasemapOpt.CompletionSources.Add("osm", "satellite", "demotiles", "none");
        root.Add(mapBasemapOpt);
        root.Add(new Option<string[]>("--disable-plugin")
        {
            Description = BowireLocale.T("cli.bowire-cli.disable-plugin"),
            AllowMultipleArgumentsPerToken = true,
        });
        var disableCliCommandOpt = new Option<string[]>("--disable-cli-command")
        {
            Description = BowireLocale.T("cli.bowire-cli.disable-cli-command"),
            AllowMultipleArgumentsPerToken = true,
        };
        root.Add(disableCliCommandOpt);

        root.SetAction(async (pr, ct) =>
            await BrowserUiHost.RunAsync(originalArgs, cfg, plugins,
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct).ConfigureAwait(false));

        root.Add(BuildDiscoverCommand(cfg));
        root.Add(BuildListCommand(cfg));
        root.Add(BuildDescribeCommand(cfg));
        root.Add(BuildCallCommand(cfg));
        root.Add(Send.SendCommand.Build(cfg));
        root.Add(BuildMockCommand(cfg, plugins));
        root.Add(BuildMcpCommand());
        root.Add(BuildPluginCommand(cfg, pluginDir));
        root.Add(BuildTestCommand(cfg));
        root.Add(BuildImportCommand());
        root.Add(BuildJwtCommand());
        root.Add(BuildFuzzCommand());
        root.Add(BuildProxyCommand());
        root.Add(BuildInterceptorCommand());
        root.Add(ExportCommand.Build());
        root.Add(DiffCommand.Build());
        root.Add(LintCommand.Build());
        // #177 - a CRUD service from a sentence or a spec.
        root.Add(ScaffoldCommand.Build());
        root.Add(WorkspaceCommand.Build());
        root.Add(CookiesCommand.Build());
        // #97 — the operator's side of per-identity storage: what is on disk,
        // and moving a single-user install's data into a named subject's slot.
        root.Add(UsersCommand.Build());
        root.Add(CatalogueCommand.Build());
        root.Add(RecordingCommand.Build());
        root.Add(ContractCommand.Build());
        root.Add(BenchCommand.Build());
        root.Add(ReportCommand.Build());
        root.Add(AuthRecordingCommand.Build());
        root.Add(ProjectCommand.Build());
        // #117 - `bowire docs translations` scaffolds and checks a UI locale.
        root.Add(TranslationsCommand.Build());
        root.Add(BuildVersionCommand(plugins));

        // `send` reaches none of the contributed subcommands below, and the
        // assembly scan is most of what its startup would cost (#678).
        if (IsSendCommand(originalArgs)) return root;

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

        // #102 — force-load the Monitoring assembly so its `bowire monitor`
        // IBowireCliCommand is visible to the assembly scan below (same reason
        // as the Scanner force-load above).
        try { _ = Activator.CreateInstance<Kuestenlogik.Bowire.Monitoring.Cli.MonitorCliCommand>(); }
        catch { /* discovery loop below surfaces the real error */ }

        // #35 Phase 2f — force-load the Oast assembly so `bowire oast serve` is
        // visible to the scan. The Scanner references Oast for the client, but
        // that reference is only touched when a scan actually uses
        // --oast-server, so the assembly is not loaded at command-build time.
        try { _ = Activator.CreateInstance<Kuestenlogik.Bowire.Oast.Cli.OastCliCommand>(); }
        catch { /* discovery loop below surfaces the real error */ }

        var disabledCli = PreparseRepeatableArg(originalArgs, "--disable-cli-command");
        foreach (var cmd in BowireCliCommandRegistry.Discover(disabledCli).Commands)
        {
            root.Add(cmd.Build(cfg));
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

    /// <summary>Ask the OS for any free port. See <c>--port-file</c> (#615).</summary>
    private const int EphemeralPort = 0;

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
    /// <param name="opt">The option to validate.</param>
    /// <param name="allowEphemeral">
    /// Also accept <c>0</c>, which asks the OS for any free port. Only the
    /// listeners that can report back where they landed — the browser UI, via
    /// <c>--port-file</c> (#615) — opt into this; on a port nobody can
    /// discover afterwards, 0 is a typo rather than a request.
    /// </param>
    private static Option<int?> WithPortValidation(this Option<int?> opt, bool allowEphemeral = false)
    {
        opt.Validators.Add(result =>
        {
            if (result.Implicit) return;
            if (result.GetValueOrDefault<int?>() is int port)
                CheckPort(result, opt.Name, port, allowEphemeral);
        });
        return opt;
    }

    private static void CheckPort(OptionResult result, string name, int port, bool allowEphemeral = false)
    {
        if (allowEphemeral && port == EphemeralPort) return;

        if (port < MinPort || port > MaxPort)
        {
            result.AddError(allowEphemeral
                ? $"{name}: port must be 0 (any free port) or between {MinPort} and {MaxPort} (got {port})."
                : $"{name}: port must be between {MinPort} and {MaxPort} (got {port}).");
        }
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
            BowireLocale.T("cli.bowire-cli.proxy.command"));

        var portOpt = new Option<int>("--port") { Description = BowireLocale.T("cli.bowire-cli.port-2") }.WithPortValidation();
        var apiPortOpt = new Option<int>("--api-port") { Description = BowireLocale.T("cli.bowire-cli.api-port") }.WithPortValidation();
        var capacityOpt = new Option<int>("--capacity") { Description = BowireLocale.T("cli.bowire-cli.capacity") };
        var noMitmOpt = new Option<bool>("--no-mitm") { Description = BowireLocale.T("cli.bowire-cli.no-mitm") };
        var caDirOpt = new Option<string?>("--ca-dir") { Description = BowireLocale.T("cli.bowire-cli.ca-dir") };
        var exportCaOpt = new Option<string?>("--export-ca") { Description = BowireLocale.T("cli.bowire-cli.export-ca") };

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
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error,
                cancellationToken: ct).ConfigureAwait(false);
        });

        return proxy;
    }

    // -------------------- interceptor --------------------

    private static Command BuildInterceptorCommand()
    {
        var interceptor = new Command("interceptor",
            BowireLocale.T("cli.bowire-cli.interceptor.command"));

        var upstreamOpt = new Option<string>("--upstream")
        {
            Description = BowireLocale.T("cli.bowire-cli.upstream"),
            Required = true,
        };
        var listenOpt = new Option<string>("--listen")
        {
            Description = BowireLocale.T("cli.bowire-cli.listen"),
        };
        var apiPortOpt = new Option<int?>("--api-port") { Description = BowireLocale.T("cli.bowire-cli.api-port-2") }.WithPortValidation();
        var capacityOpt = new Option<int?>("--capacity") { Description = BowireLocale.T("cli.bowire-cli.capacity") };
        var maxBodyOpt = new Option<int?>("--max-body-bytes") { Description = BowireLocale.T("cli.bowire-cli.max-body-bytes") };
        var allowSelfSignedOpt = new Option<bool>("--allow-self-signed-upstream")
        {
            Description = BowireLocale.T("cli.bowire-cli.allow-self-signed-upstream")
        };
        var tlsOpt = new Option<bool>("--tls")
        {
            Description = BowireLocale.T("cli.bowire-cli.tls")
        };
        var tlsHostOpt = new Option<string?>("--tls-host")
        {
            Description = BowireLocale.T("cli.bowire-cli.tls-host")
        };
        var caDirOpt = new Option<string?>("--ca-dir")
        {
            Description = BowireLocale.T("cli.bowire-cli.ca-dir-2")
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
            BowireLocale.T("cli.bowire-cli.fuzz.command"));

        var targetOpt = new Option<string>("--target") { Description = BowireLocale.T("cli.bowire-cli.target"), Required = true };
        var templateOpt = new Option<string>("--template") { Description = BowireLocale.T("cli.bowire-cli.template"), Required = true };
        var fieldOpt = new Option<string>("--field") { Description = BowireLocale.T("cli.bowire-cli.field"), Required = true };
        var categoryOpt = new Option<string>("--payloads") { Description = BowireLocale.T("cli.bowire-cli.payloads"), Required = true };
        categoryOpt.CompletionSources.Add("sqli", "xss", "pathtrav", "cmdinj");
        var forceOpt = new Option<bool>("--force") { Description = BowireLocale.T("cli.bowire-cli.force") };
        var timeoutOpt = new Option<int>("--timeout") { Description = BowireLocale.T("cli.bowire-cli.timeout") };
        var allowSelfSignedOpt = new Option<bool>("--allow-self-signed-certs") { Description = BowireLocale.T("cli.bowire-cli.allow-self-signed-certs") };
        var authHeaderOpt = new Option<string[]>("--auth-header")
        {
            Description = BowireLocale.T("cli.bowire-cli.auth-header"),
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

    // -------------------- version --------------------

    // `bowire version [--plugins]` — the running version (same value as the
    // root's --version option) plus, optionally, the loaded protocol plugins
    // and their versions. `bowire plugin list` covers directory-installed
    // packages in more detail.
    private static Command BuildVersionCommand(IBowirePluginLoader plugins)
    {
        var pluginsOpt = new Option<bool>("--plugins")
        {
            Description = BowireLocale.T("cli.bowire-cli.plugins"),
        };
        var version = new Command("version",
            BowireLocale.T("cli.bowire-cli.version.command"));
        version.Add(pluginsOpt);
        version.SetAction(async (pr, _) =>
        {
            var includePlugins = pr.GetValue(pluginsOpt);
            IEnumerable<IBowireProtocol> protocols = includePlugins
                ? plugins.EnumerateServices<IBowireProtocol>()
                : Array.Empty<IBowireProtocol>();
            await pr.InvocationConfiguration.Output.WriteLineAsync(
                BowireVersionReport.Render(includePlugins, protocols)).ConfigureAwait(false);
            return 0;
        });
        return version;
    }

    // -------------------- jwt --------------------

    private static Command BuildJwtCommand()
    {
        var jwt = new Command("jwt",
            BowireLocale.T("cli.bowire-cli.jwt.command"));

        var tokenArg = new Argument<string>("token") { Description = BowireLocale.T("cli.bowire-cli.token") };

        var decode = new Command("decode", BowireLocale.T("cli.bowire-cli.decode.command"));
        decode.Add(tokenArg);
        decode.SetAction(async (pr, ct) =>
            await JwtCommand.RunDecodeAsync(pr.GetValue(tokenArg) ?? "",
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct).ConfigureAwait(false));

        var analyze = new Command("analyze", BowireLocale.T("cli.bowire-cli.analyze.command"));
        var audienceOpt = new Option<string>("--audience", "-a") { Description = BowireLocale.T("cli.bowire-cli.audience") };
        analyze.Add(tokenArg);
        analyze.Add(audienceOpt);
        analyze.SetAction(async (pr, ct) =>
            await JwtCommand.RunAnalyzeAsync(pr.GetValue(tokenArg) ?? "", pr.GetValue(audienceOpt),
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct).ConfigureAwait(false));

        var tamper = new Command("tamper", BowireLocale.T("cli.bowire-cli.tamper.command"));
        var algNoneOpt = new Option<bool>("--alg-none") { Description = BowireLocale.T("cli.bowire-cli.alg-none") };
        var setOpt = new Option<string[]>("--set")
        {
            Description = BowireLocale.T("cli.bowire-cli.set"),
            AllowMultipleArgumentsPerToken = true,
        };
        var secretOpt = new Option<string>("--secret") { Description = BowireLocale.T("cli.bowire-cli.secret") };
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
        jwt.Add(analyze);
        jwt.Add(tamper);
        return jwt;
    }

    // scan: contributed via IBowireCliCommand from
    // Kuestenlogik.Bowire.Security.Scanner — picked up by the auto-
    // discovery loop above. See ScanCliCommand.Build().

    // -------------------- import --------------------

    private static Command BuildImportCommand()
    {
        var fileArg = new Argument<string>("file") { Description = BowireLocale.T("cli.bowire-cli.file") };
        var outOpt = new Option<string?>("--out", "-o")
        {
            Description = BowireLocale.T("cli.bowire-cli.out")
        };
        var nameOpt = new Option<string?>("--name", "-n")
        { Description = BowireLocale.T("cli.bowire-cli.name") };
        var redactOpt = new Option<bool>("--redact-secrets")
        { Description = BowireLocale.T("cli.bowire-cli.redact-secrets") };

        var har = new Command("har", BowireLocale.T("cli.bowire-cli.har.command"));
        har.Add(fileArg); har.Add(outOpt); har.Add(nameOpt); har.Add(redactOpt);
        har.SetAction(async (pr, _) =>
        {
            var input = pr.GetValue(fileArg) ?? "";
            var output = pr.GetValue(outOpt);
            var name = pr.GetValue(nameOpt);
            var redact = pr.GetValue(redactOpt);
            // Sensible default: drop the .bwr next to the HAR with the same
            // basename. Stays predictable regardless of cwd.
            if (string.IsNullOrEmpty(output))
            {
                var basename = Path.GetFileNameWithoutExtension(input);
                var dir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? "";
                output = Path.Combine(dir, basename + ".bwr");
            }
            return await HarImporter.ImportAsync(input, output, name,
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, redact).ConfigureAwait(false);
        });

        var import = new Command("import",
            BowireLocale.T("cli.bowire-cli.import.command"));
        import.Add(har);
        InteropCommands.AttachImport(import);
        return import;
    }

    // -------------------- discover / list / describe / call --------------------
    // CliCommandOptions binds Url / Protocol / Plaintext / Verbose / Compact +
    // positional Target, repeated -d / -H. All four subcommands share
    // the option set; only the action handler differs. `call` layers four
    // more options of its own (#538) — see BuildCallCommand.

    private static (Option<string> url, Option<bool> plaintext, Option<bool> verbose, Option<bool> compact,
        Option<string[]> data, Option<string[]> headers) GrpcCliOptions(IConfiguration cfg)
    {
        var url = new Option<string>("--url", "-url")
        {
            Description = BowireLocale.T("cli.bowire-cli.url-2"),
            DefaultValueFactory = _ => cfg["Bowire:Cli:Url"] ?? "https://localhost:5001"
        };
        var plaintext = new Option<bool>("-plaintext", "--plaintext")
        {
            Description = BowireLocale.T("cli.bowire-cli.plaintext"),
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Cli:Plaintext")
        };
        var verbose = new Option<bool>("-v", "--verbose")
        {
            Description = BowireLocale.T("cli.bowire-cli.v"),
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Cli:Verbose")
        };
        var compact = new Option<bool>("--compact")
        {
            Description = BowireLocale.T("cli.bowire-cli.compact"),
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Cli:Compact")
        };
        var data = new Option<string[]>("-d", "--data")
        { Description = BowireLocale.T("cli.bowire-cli.d") };
        var headers = new Option<string[]>("-H")
        { Description = BowireLocale.T("cli.bowire-cli.h") };
        return (url, plaintext, verbose, compact, data, headers);
    }

    /// <summary>
    /// <c>--grpc-descriptor-set</c>, for every gRPC-capable subcommand.
    /// </summary>
    /// <remarks>
    /// One definition rather than four: the four commands that reach a gRPC
    /// server (<c>list</c>, <c>describe</c>, <c>discover</c>, <c>call</c>) all
    /// need it, and a flag that means something subtly different depending on
    /// which verb you typed is worse than no flag.
    /// </remarks>
    private static Option<string?> GrpcDescriptorSetOption() => new("--grpc-descriptor-set")
    {
        Description = BowireLocale.T("cli.bowire-cli.description-2"),
    };

    /// <summary>
    /// Fold the flag into the metadata the plugin already reads.
    /// </summary>
    /// <remarks>
    /// An explicit <c>-H</c> of the same key wins: a caller who spelled the
    /// marker out was being deliberate, and silently overwriting that would
    /// make the more precise instruction lose to the more convenient one.
    /// </remarks>
    private static void ApplyDescriptorSet(ParseResult pr, Option<string?> option, CliCommandOptions cli)
    {
        var path = pr.GetValue(option);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (cli.Headers.Exists(h => h.StartsWith(BowireMetadataKeys.GrpcDescriptorSet, StringComparison.Ordinal))) return;
        cli.Headers.Add($"{BowireMetadataKeys.GrpcDescriptorSet}: {path}");
    }

    // #679 - challenge-response authentication for `call`, curl's spelling.
    private static (Option<bool> Negotiate, Option<string?> Ntlm, Option<string?> Digest) HttpAuthOptions() => (
        new Option<bool>("--negotiate")
        {
            Description = BowireLocale.T("cli.bowire-cli.negotiate"),
        },
        new Option<string?>("--ntlm")
        {
            Description = BowireLocale.T("cli.bowire-cli.ntlm"),
        },
        new Option<string?>("--digest")
        {
            Description = BowireLocale.T("cli.bowire-cli.digest"),
        });

    /// <summary>
    /// Fold <c>--negotiate</c> / <c>--ntlm</c> / <c>--digest</c> into the
    /// metadata marker every HTTP-based plugin reads. An explicit <c>-H</c>
    /// of the marker wins, as with <see cref="ApplyDescriptorSet"/>.
    /// </summary>
    internal static void ApplyHttpAuth(bool negotiate, string? ntlm, string? digest, CliCommandOptions cli)
    {
        if (cli.Headers.Exists(h => h.StartsWith(Kuestenlogik.Bowire.Auth.BowireHttpAuth.MarkerKey, StringComparison.Ordinal))) return;
        object? marker = null;
        if (negotiate)
        {
            marker = new { scheme = "negotiate" };
        }
        else if (!string.IsNullOrEmpty(ntlm) || !string.IsNullOrEmpty(digest))
        {
            var credentials = ntlm ?? digest!;
            var colon = credentials.IndexOf(':', StringComparison.Ordinal);
            var user = colon < 0 ? credentials : credentials[..colon];
            var password = colon < 0 ? string.Empty : credentials[(colon + 1)..];
            marker = new { scheme = ntlm is not null ? "ntlm" : "digest", user, password };
        }
        if (marker is null) return;
        cli.Headers.Add(Kuestenlogik.Bowire.Auth.BowireHttpAuth.MarkerKey + ": "
            + System.Text.Json.JsonSerializer.Serialize(marker));
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
        // #538 — split `hint@url` ONCE, here, so everything downstream
        // sees a bare URL plus a typed Protocol. Two things depended on
        // this being done before anything else touches the value:
        //   * the plaintext downgrade below matched on StartsWith("https://"),
        //     which never fired for `grpc@https://…` — a latent bug;
        //   * DiscoverImplAsync used to re-parse the hint itself, and a
        //     second parser is exactly the kind of duplicate this repo
        //     avoids. BowireServerUrl.Parse owns the three
        //     userinfo/email disambiguation rules.
        var (hint, bareUrl) = BowireServerUrl.Parse(options.Url);
        options.Url = bareUrl;
        options.Protocol = hint;
        // Plaintext URL downgrade: callers shouldn't have to do the
        // substitution themselves. Same one-line policy as
        // BowireConfiguration.BuildCliOptions used to apply.
        if (options.Plaintext && options.Url.StartsWith("https://", StringComparison.Ordinal))
            options.Url = "http://" + options.Url["https://".Length..];
        return options;
    }

    // #534 — the multi-protocol counterpart to `list`. `bowire list` stays
    // gRPC-reflection-only (scripts depend on its output shape); this one
    // fans out over every loaded plugin and always prints the per-plugin
    // attempt table, so a headless / CI user gets the same diagnosis the
    // workbench shows in its Sources rail. Exit 0 when at least one
    // service was found, 1 otherwise, so CI can gate on it.
    /// <summary>
    /// <c>--schema</c> — discover from a document on disk instead of from a
    /// server, or in addition to one (#654).
    /// </summary>
    /// <remarks>
    /// Repeatable, because a service is often described by more than one
    /// file. The workbench's equivalent is dropping the file on the sidebar,
    /// which stores it; a CLI run names it instead, so a pipeline does not
    /// depend on what somebody uploaded on another machine.
    /// </remarks>
    // The loaded plugins: they declare the schema kinds --schema takes and
    // read a named file once before the command runs (#1018).
    private static readonly Lazy<BowireProtocolRegistry> s_plugins =
        new(() => BowireProtocolRegistry.Discover());

    // `list` and `describe` are gRPC-only, so a schema of another plugin's
    // kind is refused there rather than accepted and not used; `discover`
    // lists it.
    private static readonly CliSchemaInputs.Taken s_grpcOnly =
        CliSchemaInputs.Taken.Only(SchemaUploadStore.ProtoSetKind);

    private static Option<string[]> SchemaOption() => new("--schema")
    {
        Description = BowireLocale.T("cli.bowire-cli.description-3"),
        AllowMultipleArgumentsPerToken = true,
    };

    // internal so the CLI-grammar tests can parse it without booting a host.
    internal static Command BuildDiscoverCommand(IConfiguration cfg)
    {
        var (url, plaintext, verbose, _, _, _) = GrpcCliOptions(cfg);
        var cmd = new Command("discover",
            BowireLocale.T("cli.bowire-cli.discover.command"));
        var descriptorSet = GrpcDescriptorSetOption();
        var schemaFiles = SchemaOption();
        cmd.Add(url); cmd.Add(plaintext); cmd.Add(verbose); cmd.Add(descriptorSet); cmd.Add(schemaFiles);
        cmd.SetAction(async (pr, _) =>
        {
            var (given, code) = await CliSchemaInputs.ReadAsync(
                pr.GetValue(schemaFiles), pr.InvocationConfiguration.Error,
                s_plugins.Value).ConfigureAwait(false);
            if (code != 0) return code;
            // Opened here, not in the helper: an AsyncLocal set inside an
            // async method does not survive its return (#654).
            using (given is null ? null : SchemaUploadStore.EnterExplicit(given))
            {
                var cli = BuildCliOptions(pr, url, plaintext, verbose, new Option<bool>("--compact"), null, null, null);
                ApplyDescriptorSet(pr, descriptorSet, cli);
                var discovered = await CliHandler.DiscoverAsync(cli,
                    pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false);
                StarHint.MaybeShow(discovered, cli.Compact, pr.InvocationConfiguration.Error);
                return discovered;
            }
        });
        return cmd;
    }

    // internal so the CLI-grammar tests can parse it without booting a host.
    internal static Command BuildListCommand(IConfiguration cfg)
    {
        var (url, plaintext, verbose, _, _, _) = GrpcCliOptions(cfg);
        var descriptorSet = GrpcDescriptorSetOption();
        var schemaFiles = SchemaOption();
        var cmd = new Command("list", BowireLocale.T("cli.bowire-cli.list.command"));
        cmd.Add(url); cmd.Add(plaintext); cmd.Add(verbose); cmd.Add(descriptorSet); cmd.Add(schemaFiles);
        cmd.SetAction(async (pr, _) =>
        {
            var (given, code) = await CliSchemaInputs.ReadAsync(
                pr.GetValue(schemaFiles), pr.InvocationConfiguration.Error,
                s_plugins.Value, s_grpcOnly).ConfigureAwait(false);
            if (code != 0) return code;
            // Opened here, not in the helper: an AsyncLocal set inside an
            // async method does not survive its return (#654).
            using (given is null ? null : SchemaUploadStore.EnterExplicit(given))
            {
                var cli = BuildCliOptions(pr, url, plaintext, verbose, new Option<bool>("--compact"), null, null, null);
                ApplyDescriptorSet(pr, descriptorSet, cli);
                return await CliHandler.ListAsync(cli,
                    pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false);
            }
        });
        return cmd;
    }

    // internal so the CLI-grammar tests can parse it without booting a host.
    internal static Command BuildDescribeCommand(IConfiguration cfg)
    {
        var (url, plaintext, verbose, _, _, _) = GrpcCliOptions(cfg);
        var target = new Argument<string>("target") { Description = BowireLocale.T("cli.bowire-cli.target-2") };

        var descriptorSet = GrpcDescriptorSetOption();
        var schemaFiles = SchemaOption();
        var cmd = new Command("describe", BowireLocale.T("cli.bowire-cli.describe.command"));
        cmd.Add(target); cmd.Add(url); cmd.Add(plaintext); cmd.Add(verbose); cmd.Add(descriptorSet); cmd.Add(schemaFiles);
        cmd.SetAction(async (pr, _) =>
        {
            var (given, code) = await CliSchemaInputs.ReadAsync(
                pr.GetValue(schemaFiles), pr.InvocationConfiguration.Error,
                s_plugins.Value, s_grpcOnly).ConfigureAwait(false);
            if (code != 0) return code;
            // Opened here, not in the helper: an AsyncLocal set inside an
            // async method does not survive its return (#654).
            using (given is null ? null : SchemaUploadStore.EnterExplicit(given))
            {
                var cli = BuildCliOptions(
                    pr, url, plaintext, verbose, new Option<bool>("--compact"), null, null, pr.GetValue(target));
                ApplyDescriptorSet(pr, descriptorSet, cli);
                return await CliHandler.DescribeAsync(cli,
                    pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false);
            }
        });
        return cmd;
    }

    /// <summary>
    /// <c>bowire call</c> — protocol-generic since #538. gRPC keeps a fast
    /// path (no assembly scan) so existing scripts pay nothing; anything
    /// else routes through the plugin registry.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than <c>private</c> for the same reason as
    /// <see cref="BuildMockCommand"/> and <see cref="BuildFuzzCommand"/>:
    /// the CLI-grammar tests parse this command directly instead of
    /// booting a host. <c>CliExportGrammarTests</c> in particular replays
    /// every argv the workbench's "Copy as Bowire CLI" exporter emits
    /// through <c>Parse(...)</c> and fails when a flag is renamed or
    /// removed — that pair is what keeps the generated command honest.
    /// </remarks>
    internal static Command BuildCallCommand(IConfiguration cfg)
    {
        var (url, plaintext, verbose, compact, data, headers) = GrpcCliOptions(cfg);
        var target = new Argument<string>("target") { Description = BowireLocale.T("cli.bowire-cli.target-3") };

        // #538 — the four options that make `call` more than a grpcurl
        // clone. Declared locally rather than on the shared
        // GrpcCliOptions tuple on purpose: `list` / `describe` stay
        // gRPC-reflection-only and must not grow a --protocol that does
        // nothing.
        var protocol = new Option<string?>("--protocol")
        {
            Description = BowireLocale.T("cli.bowire-cli.protocol"),
        };
        protocol.CompletionSources.Add("grpc", "rest", "graphql", "mcp", "sse", "websocket",
            "signalr", "mqtt", "nats", "odata", "soap", "jsonrpc", "socketio");
        var stream = new Option<bool>("--stream")
        {
            Description = BowireLocale.T("cli.bowire-cli.stream"),
        };
        var vars = new Option<string[]>("--var", "--env")
        {
            Description = BowireLocale.T("cli.bowire-cli.var"),
            AllowMultipleArgumentsPerToken = false,
        };
        var varFiles = new Option<string[]>("--env-file")
        {
            Description = BowireLocale.T("cli.bowire-cli.env-file"),
            AllowMultipleArgumentsPerToken = false,
        };

        var grpcDescriptorSet = GrpcDescriptorSetOption();
        var (negotiate, ntlm, digest) = HttpAuthOptions();
        var cmd = new Command("call",
            BowireLocale.T("cli.bowire-cli.call.command"));
        cmd.Add(target); cmd.Add(url); cmd.Add(plaintext); cmd.Add(verbose);
        cmd.Add(compact); cmd.Add(data); cmd.Add(headers);
        cmd.Add(protocol); cmd.Add(stream); cmd.Add(vars); cmd.Add(varFiles);
        cmd.Add(grpcDescriptorSet);
        cmd.Add(negotiate); cmd.Add(ntlm); cmd.Add(digest);
        // `call` resolves the method by discovering first, so a schema named
        // here is what lets it invoke against a service the server does not
        // describe (#654).
        var schemaFiles = SchemaOption();
        cmd.Add(schemaFiles);
        // ct rather than `_`: --stream blocks until the server ends the
        // stream, so Ctrl+C has to reach InvokeStreamAsync.
        cmd.SetAction(async (pr, ct) =>
        {
            var (given, code) = await CliSchemaInputs.ReadAsync(
                pr.GetValue(schemaFiles), pr.InvocationConfiguration.Error,
                s_plugins.Value, CliSchemaInputs.Taken.ForCalls).ConfigureAwait(false);
            if (code != 0) return code;
            // Opened here, not in the helper: an AsyncLocal set inside an
            // async method does not survive its return (#654).
            using (given is null ? null : SchemaUploadStore.EnterExplicit(given))
            {
                var cli = BuildCliOptions(
                    pr, url, plaintext, verbose, compact, data, headers, pr.GetValue(target));
                // Explicit --protocol beats the hint BuildCliOptions parsed
                // off the URL; a hint is a convenience, the flag is a
                // statement.
                var explicitProtocol = pr.GetValue(protocol);
                if (!string.IsNullOrEmpty(explicitProtocol)) cli.Protocol = explicitProtocol;
                cli.Stream = pr.GetValue(stream);
                ApplyDescriptorSet(pr, grpcDescriptorSet, cli);
                ApplyHttpAuth(pr.GetValue(negotiate), pr.GetValue(ntlm), pr.GetValue(digest), cli);
                cli.Vars.AddRange(pr.GetValue(vars) ?? []);
                cli.VarFiles.AddRange(pr.GetValue(varFiles) ?? []);
                var called = await CliHandler.CallAsync(cli,
                    pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct).ConfigureAwait(false);
                // #669 - once per machine, after a call that worked.
                StarHint.MaybeShow(called, cli.Compact, pr.InvocationConfiguration.Error);
                return called;
            }
        });
        return cmd;
    }

    // -------------------- mock --------------------

    // internal so the tests in Kuestenlogik.Bowire.Tests can exercise
    // the System.CommandLine wiring (the #211 positional shape +
    // mutex against --recording / --schema) without booting a real
    // mock server.
    internal static Command BuildMockCommand(IConfiguration cfg, IBowirePluginLoader plugins)
    {
        var recording = new Option<string?>("--recording", "-r")
        {
            Description = BowireLocale.T("cli.bowire-cli.recording"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:RecordingPath"]
        }.WithExistingFileValidation();
        var schema = new Option<string?>("--schema")
        {
            Description = BowireLocale.T("cli.bowire-cli.schema"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:SchemaPath"]
        };
        var grpcSchema = new Option<string?>("--grpc-schema")
        {
            Description = BowireLocale.T("cli.bowire-cli.grpc-schema"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:GrpcSchemaPath"]
        };
        var graphqlSchema = new Option<string?>("--graphql-schema")
        {
            Description = BowireLocale.T("cli.bowire-cli.graphql-schema"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:GraphQlSchemaPath"]
        };
        var port = new Option<int>("--port")
        {
            Description = BowireLocale.T("cli.bowire-cli.port-3"),
            DefaultValueFactory = _ => cfg.GetValue<int?>("Bowire:Mock:Port") ?? 6000
        }.WithPortValidation();
        var host = new Option<string>("--host")
        {
            Description = BowireLocale.T("cli.bowire-cli.host"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:Host"] ?? "127.0.0.1"
        };
        var https = new Option<bool>("--https")
        {
            Description = BowireLocale.T("cli.bowire-cli.https"),
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Mock:Https")
        };
        var httpsPort = new Option<int?>("--https-port")
        {
            Description = BowireLocale.T("cli.bowire-cli.https-port"),
            DefaultValueFactory = _ => cfg.GetValue<int?>("Bowire:Mock:HttpsPort")
        };
        var cert = new Option<string?>("--cert")
        {
            Description = BowireLocale.T("cli.bowire-cli.cert"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:CertPath"]
        };
        cert.Validators.Add(result =>
        {
            var path = result.GetValueOrDefault<string?>();
            if (!string.IsNullOrEmpty(path) && !File.Exists(path))
                result.AddError($"--cert: file not found: '{path}'.");
        });
        var certPassword = new Option<string?>("--cert-password")
        {
            Description = BowireLocale.T("cli.bowire-cli.cert-password"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:CertPassword"]
        };
        var proxy = new Option<string?>("--proxy")
        {
            Description = BowireLocale.T("cli.bowire-cli.proxy"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:ProxyBaseUrl"]
        };
        var proxyRecord = new Option<string?>("--proxy-record")
        {
            Description = BowireLocale.T("cli.bowire-cli.proxy-record"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:ProxyRecordPath"]
        };
        var select = new Option<string?>("--select")
        {
            Description = BowireLocale.T("cli.bowire-cli.select"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:Select"]
        };
        var noWatch = new Option<bool>("--no-watch")
        {
            Description = BowireLocale.T("cli.bowire-cli.no-watch"),
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Mock:NoWatch")
        };
        var stateful = new Option<bool>("--stateful")
        {
            Description = BowireLocale.T("cli.bowire-cli.stateful"),
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Mock:Stateful")
        };
        var statefulOnce = new Option<bool>("--stateful-once")
        {
            Description = BowireLocale.T("cli.bowire-cli.stateful-once"),
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Mock:StatefulOnce")
        };
        var loop = new Option<bool>("--loop")
        {
            Description = BowireLocale.T("cli.bowire-cli.loop"),
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Mock:Loop")
        };
        // #708 — nullable, and no default factory: an unset interval has to
        // stay distinguishable from one the operator typed, because 0 is the
        // one value that asks for an unbounded rate and it cannot be a
        // default that nobody chose.
        var loopInterval = new Option<int?>("--loop-interval-ms")
        {
            Description = BowireLocale.T("cli.bowire-cli.loop-interval-ms"),
            DefaultValueFactory = _ =>
            {
                var configured = cfg["Bowire:Mock:LoopIntervalMs"];
                return int.TryParse(configured, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : null;
            }
        };
        loopInterval.Validators.Add(r =>
        {
            var v = r.GetValue(loopInterval);
            if (v is < 0) r.AddError("--loop-interval-ms cannot be negative.");
        });
        var autoInstall = new Option<bool>("--auto-install")
        {
            Description = BowireLocale.T("cli.bowire-cli.auto-install"),
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Mock:AutoInstall")
        };
        var chaos = new Option<string?>("--chaos")
        {
            Description = BowireLocale.T("cli.bowire-cli.chaos"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:Chaos"]
        }.WithChaosValidation();
        var faults = new Option<string?>("--faults")
        {
            Description = BowireLocale.T("cli.bowire-cli.faults"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:FaultsPath"]
        };
        faults.Validators.Add(result =>
        {
            var path = result.GetValueOrDefault<string?>();
            if (!string.IsNullOrEmpty(path) && !File.Exists(path))
                result.AddError($"--faults: file not found: '{path}'.");
        });
        var captureMiss = new Option<string?>("--capture-miss")
        {
            Description = BowireLocale.T("cli.bowire-cli.capture-miss"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:CaptureMissPath"]
        };
        var mockConfig = new Option<string?>("--mock-config")
        {
            Description = BowireLocale.T("cli.bowire-cli.mock-config"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:MockConfigPath"]
        };
        mockConfig.Validators.Add(result =>
        {
            var path = result.GetValueOrDefault<string?>();
            if (!string.IsNullOrEmpty(path) && !File.Exists(path))
                result.AddError($"--mock-config: file not found: '{path}'.");
        });
        var controlToken = new Option<string?>("--control-token")
        {
            Description = BowireLocale.T("cli.bowire-cli.control-token"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:ControlToken"]
        };
        var requireAuth = new Option<string?>("--require-auth")
        {
            Description = BowireLocale.T("cli.bowire-cli.require-auth"),
            DefaultValueFactory = _ => cfg["Bowire:Mock:RequireAuth"]
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
            Description = BowireLocale.T("cli.bowire-cli.path"),
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

        var cmd = new Command("mock", BowireLocale.T("cli.bowire-cli.mock.command"));
        cmd.Add(positionalPath);
        cmd.Add(recording); cmd.Add(schema); cmd.Add(grpcSchema); cmd.Add(graphqlSchema);
        cmd.Add(port); cmd.Add(host); cmd.Add(https); cmd.Add(httpsPort);
        cmd.Add(cert); cmd.Add(certPassword); cmd.Add(proxy); cmd.Add(proxyRecord); cmd.Add(select); cmd.Add(noWatch);
        cmd.Add(stateful); cmd.Add(statefulOnce); cmd.Add(loop); cmd.Add(loopInterval); cmd.Add(autoInstall);
        cmd.Add(chaos); cmd.Add(faults); cmd.Add(captureMiss); cmd.Add(controlToken); cmd.Add(mockConfig); cmd.Add(requireAuth);
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
                Https = pr.GetValue(https),
                HttpsPort = pr.GetValue(httpsPort),
                CertPath = pr.GetValue(cert),
                CertPassword = pr.GetValue(certPassword),
                ProxyBaseUrl = pr.GetValue(proxy),
                ProxyRecordPath = pr.GetValue(proxyRecord),
                Select = pr.GetValue(select),
                NoWatch = pr.GetValue(noWatch),
                Stateful = pr.GetValue(stateful),
                StatefulOnce = pr.GetValue(statefulOnce),
                Loop = pr.GetValue(loop),
                LoopIntervalMs = pr.GetValue(loopInterval),
                AutoInstall = pr.GetValue(autoInstall),
                Chaos = pr.GetValue(chaos),
                FaultsPath = pr.GetValue(faults),
                CaptureMissPath = pr.GetValue(captureMiss),
                ControlToken = pr.GetValue(controlToken),
                MockConfigPath = pr.GetValue(mockConfig),
                RequireAuth = pr.GetValue(requireAuth)
            };
            return await MockCommand.RunAsync(options, plugins,
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct).ConfigureAwait(false);
        });
        return cmd;
    }

    // -------------------- mcp (typed since the SDK migration) --------------------

    private static Command BuildMcpCommand()
    {
        var bind = new Option<string>("--bind")
        {
            Description = BowireLocale.T("cli.bowire-cli.bind"),
            DefaultValueFactory = _ => "stdio"
        };
        bind.AcceptOnlyFromAmong("stdio", "http");

        // #635 — nullable, and no default factory: an unset --port has to stay
        // distinguishable from one the operator typed, because only the second
        // may override ASPNETCORE_URLS / Kestrel:Endpoints. Same shape as the
        // root command's --port.
        var port = new Option<int?>("--port")
        {
            Description = BowireLocale.T("cli.bowire-cli.port-4"),
        }.WithPortValidation();
        var allowArbitrary = new Option<bool>("--allow-arbitrary-urls")
        { Description = BowireLocale.T("cli.bowire-cli.allow-arbitrary-urls") };
        var noEnvAllowlist = new Option<bool>("--no-env-allowlist")
        { Description = BowireLocale.T("cli.bowire-cli.no-env-allowlist") };
        var allowInvoke = new Option<bool>("--allow-invoke")
        { Description = BowireLocale.T("cli.bowire-cli.allow-invoke") };
        var noConfirm = new Option<bool>("--no-confirm")
        { Description = BowireLocale.T("cli.bowire-cli.no-confirm") };
        var attach = new Option<string?>("--attach")
        { Description = BowireLocale.T("cli.bowire-cli.attach") };
        var attachToken = new Option<string?>("--attach-token")
        { Description = BowireLocale.T("cli.bowire-cli.attach-token") };
        var token = new Option<string?>("--token")
        { Description = BowireLocale.T("cli.bowire-cli.token-2") };

        var serve = new Command("serve", BowireLocale.T("cli.bowire-cli.serve.command"));
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

        var mcp = new Command("mcp", BowireLocale.T("cli.bowire-cli.mcp.command"));
        mcp.Add(serve);
        return mcp;
    }

    // -------------------- plugin --------------------

    private static Command BuildPluginCommand(IConfiguration cfg, string pluginDir)
    {
        var versionOpt = new Option<string?>("--version")
        {
            Description = BowireLocale.T("cli.bowire-cli.version"),
            DefaultValueFactory = _ => cfg["Bowire:Plugin:Version"]
        };
        var sourcesOpt = new Option<string[]>("--source", "-s")
        { Description = BowireLocale.T("cli.bowire-cli.source") };
        var fileOpt = new Option<string?>("--file")
        {
            Description = BowireLocale.T("cli.bowire-cli.file-2"),
            DefaultValueFactory = _ => cfg["Bowire:Plugin:File"]
        };
        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = BowireLocale.T("cli.bowire-cli.output"),
            DefaultValueFactory = _ => cfg["Bowire:Plugin:OutputDir"]
        };
        var verboseOpt = new Option<bool>("-v", "--verbose")
        {
            Description = BowireLocale.T("cli.bowire-cli.v-2"),
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Plugin:Verbose")
        };
        var packageIdArg = new Argument<string>("packageId") { Description = BowireLocale.T("cli.bowire-cli.packageid") };
        // Install accepts a packageId OR a --file path (sidecar zip /
        // oci ref / .nupkg). The shared packageIdArg above is required
        // for the download / uninstall / inspect commands; install gets
        // its own optional variant so users can write
        // `bowire plugin install --file my-sidecar.zip` without
        // inventing a dummy package id the action would just ignore.
        var installPackageIdArg = new Argument<string>("packageId")
        {
            Description = BowireLocale.T("cli.bowire-cli.packageid-2"),
            DefaultValueFactory = _ => string.Empty,
        };
        var prereleaseOpt = new Option<bool>("--prerelease")
        {
            Description = BowireLocale.T("cli.bowire-cli.prerelease"),
        };

        var install = new Command("install", BowireLocale.T("cli.bowire-cli.install.command"));
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

        var download = new Command("download", BowireLocale.T("cli.bowire-cli.download.command"));
        download.Add(packageIdArg); download.Add(versionOpt); download.Add(sourcesOpt); download.Add(outputOpt);
        download.SetAction(async (pr, _) =>
            await PluginManager.DownloadAsync(
                pr.GetValue(packageIdArg) ?? "",
                pr.GetValue(versionOpt),
                pr.GetValue(outputOpt) ?? Directory.GetCurrentDirectory(),
                pr.GetValue(sourcesOpt) ?? [],
                pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false));

        var list = new Command("list", BowireLocale.T("cli.bowire-cli.list.command-2"));
        list.Add(verboseOpt);
        list.SetAction((pr, _) => Task.FromResult(PluginManager.List(
            pluginDir, pr.GetValue(verboseOpt),
            pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error)));

        var uninstall = new Command("uninstall", BowireLocale.T("cli.bowire-cli.uninstall.command"));
        uninstall.Add(packageIdArg);
        uninstall.SetAction((pr, _) => Task.FromResult(PluginManager.Uninstall(
            pr.GetValue(packageIdArg) ?? "", pluginDir,
            pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error)));

        var updateIdArg = new Argument<string>("packageId")
        { Description = BowireLocale.T("cli.bowire-cli.packageid-3"), DefaultValueFactory = _ => "" };
        var update = new Command("update", BowireLocale.T("cli.bowire-cli.update.command"));
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

        var inspect = new Command("inspect", BowireLocale.T("cli.bowire-cli.inspect.command"));
        inspect.Add(packageIdArg);
        inspect.SetAction((pr, _) => Task.FromResult(PluginManager.Inspect(
            pr.GetValue(packageIdArg) ?? "", pluginDir,
            pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error)));

        var plugin = new Command("plugin", BowireLocale.T("cli.bowire-cli.plugin.command"));
        plugin.Add(install); plugin.Add(download); plugin.Add(list);
        plugin.Add(uninstall); plugin.Add(update); plugin.Add(inspect);
        return plugin;
    }

    // -------------------- test --------------------

    internal static Command BuildTestCommand(IConfiguration cfg)
    {
        // v2.2 — accepts EITHER a recording (legacy v2.1 test-collection
        // shape) OR a Flow JSON document (T2 deliverable). The runner
        // sniffs the JSON shape and dispatches to the right backend so
        // operators don't have to pick a flag. Positional arg therefore
        // describes both formats.
        var collectionPath = new Argument<string?>("file")
        { Description = BowireLocale.T("cli.bowire-cli.file-3"), DefaultValueFactory = _ => cfg["Bowire:Test:CollectionPath"] };
        var url = new Option<string?>("--url")
        {
            Description = BowireLocale.T("cli.bowire-cli.url-3"),
            DefaultValueFactory = _ => cfg["Bowire:Test:Url"]
        };
        var report = new Option<string?>("--report")
        {
            Description = BowireLocale.T("cli.bowire-cli.report"),
            DefaultValueFactory = _ => cfg["Bowire:Test:ReportPath"]
        };
        var junit = new Option<string?>("--junit")
        {
            Description = BowireLocale.T("cli.bowire-cli.junit"),
            DefaultValueFactory = _ => cfg["Bowire:Test:JUnitPath"]
        };
        var sarif = new Option<string?>("--sarif")
        {
            Description = BowireLocale.T("cli.bowire-cli.sarif"),
            DefaultValueFactory = _ => cfg["Bowire:Test:SarifPath"]
        };
        var annotations = new Option<bool>("--annotations")
        {
            Description = BowireLocale.T("cli.bowire-cli.annotations"),
        };
        var updateSnapshots = new Option<bool>("--update-snapshots")
        {
            Description = BowireLocale.T("cli.bowire-cli.update-snapshots"),
        };
        var failOn = new Option<string>("--fail-on")
        {
            Description = BowireLocale.T("cli.bowire-cli.fail-on"),
            DefaultValueFactory = _ => cfg["Bowire:Test:FailOn"] ?? "any",
        };
        // #583 — the severities only mean something to --suite lint; the
        // action refuses them for a test run rather than guessing.
        failOn.AcceptOnlyFromAmong("any", "never", "info", "low", "medium", "high");
        var workspaceDir = new Option<string?>("--workspace")
        {
            Description = BowireLocale.T("cli.bowire-cli.workspace"),
        };
        // #583 — the lint rules through the test runner's report sinks.
        var suite = new Option<string?>("--suite")
        {
            Description = BowireLocale.T("cli.bowire-cli.suite"),
        };
        var lintBaseline = new Option<string?>("--baseline")
        {
            Description = BowireLocale.T("cli.bowire-cli.baseline"),
        };
        var workspaceId = new Option<string?>("--workspace-id")
        {
            Description = BowireLocale.T("cli.bowire-cli.workspace-id"),
        };
        // v2.2 T2 — Flow-runner specific. Ignored for the recording
        // codepath which already carries serverUrl + environment per
        // test-collection.
        var baseUrl = new Option<string?>("--base-url")
        {
            Description = BowireLocale.T("cli.bowire-cli.base-url"),
            DefaultValueFactory = _ => cfg["Bowire:Test:BaseUrl"]
        };
        var env = new Option<string[]>("--env", "--vars")
        {
            Description = BowireLocale.T("cli.bowire-cli.env"),
            AllowMultipleArgumentsPerToken = false,
        };
        var envFile = new Option<string[]>("--env-file")
        {
            Description = BowireLocale.T("cli.bowire-cli.env-file-2"),
            AllowMultipleArgumentsPerToken = false,
            DefaultValueFactory = _ => cfg["Bowire:Test:EnvFile"] is { Length: > 0 } p ? [p] : [],
        };
        var keyring = new Option<bool>("--keyring")
        {
            Description = BowireLocale.T("cli.bowire-cli.keyring"),
            DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Test:Keyring"),
        };
        var aiSeed = new Option<string?>("--ai-seed")
        {
            Description = BowireLocale.T("cli.bowire-cli.ai-seed"),
            DefaultValueFactory = _ => cfg["Bowire:Test:AiSeed"],
        };
        var secret = new Option<string[]>("--secret")
        {
            Description = BowireLocale.T("cli.bowire-cli.secret-2"),
            AllowMultipleArgumentsPerToken = false,
        };
        var data = new Option<string?>("--data")
        {
            Description = BowireLocale.T("cli.bowire-cli.data"),
            DefaultValueFactory = _ => cfg["Bowire:Test:Data"],
        };
        var failFast = new Option<bool>("--fail-fast")
        {
            Description = BowireLocale.T("cli.bowire-cli.fail-fast"),
        };
        var secretFile = new Option<string?>("--secret-file")
        {
            Description = BowireLocale.T("cli.bowire-cli.secret-file"),
            DefaultValueFactory = _ => cfg["Bowire:Test:SecretFile"],
        };

        var cmd = new Command("test", BowireLocale.T("cli.bowire-cli.test.command"));
        cmd.Add(collectionPath); cmd.Add(url); cmd.Add(report); cmd.Add(junit);
        cmd.Add(sarif); cmd.Add(annotations); cmd.Add(updateSnapshots);
        cmd.Add(failOn); cmd.Add(workspaceDir); cmd.Add(workspaceId); cmd.Add(suite); cmd.Add(lintBaseline);
        cmd.Add(baseUrl); cmd.Add(env); cmd.Add(envFile); cmd.Add(keyring); cmd.Add(aiSeed);
        cmd.Add(secret); cmd.Add(secretFile); cmd.Add(data); cmd.Add(failFast);
        // #991 — the steps of a run are calls, and a gRPC call to a server
        // without reflection needs the descriptor set named here.
        var schemaFiles = SchemaOption();
        cmd.Add(schemaFiles);
        cmd.SetAction(async (pr, _) =>
        {
            var (given, code) = await CliSchemaInputs.ReadAsync(
                pr.GetValue(schemaFiles), pr.InvocationConfiguration.Error,
                s_plugins.Value, CliSchemaInputs.Taken.ForCalls).ConfigureAwait(false);
            if (code != 0) return code;
            // Opened here, not in the helper: an AsyncLocal set inside an
            // async method does not survive its return (#654).
            using (given is null ? null : SchemaUploadStore.EnterExplicit(given))
            {
                return await RunTestAsync(pr).ConfigureAwait(false);
            }
        });
        return cmd;

        async Task<int> RunTestAsync(ParseResult pr)
        {
            var options = new TestCliOptions
            {
                CollectionPath = pr.GetValue(collectionPath),
                Configuration = cfg,
                ReportPath = pr.GetValue(report),
                JUnitPath = pr.GetValue(junit),
                SarifPath = pr.GetValue(sarif),
                Annotations = pr.GetValue(annotations),
                UpdateSnapshots = pr.GetValue(updateSnapshots),
                FailOn = pr.GetValue(failOn) ?? "any",
                BaseUrl = pr.GetValue(baseUrl),
                EnvOverrides = pr.GetValue(env) ?? Array.Empty<string>(),
                EnvFiles = pr.GetValue(envFile) ?? Array.Empty<string>(),
                Keyring = pr.GetValue(keyring),
                AiSeed = pr.GetValue(aiSeed),
                Secrets = pr.GetValue(secret) ?? Array.Empty<string>(),
                SecretFile = pr.GetValue(secretFile),
                DataPath = pr.GetValue(data),
                FailFast = pr.GetValue(failFast),
            };
            var stdout = pr.InvocationConfiguration.Output;
            var stderr = pr.InvocationConfiguration.Error;

            // #181 — --workspace runs every flow file in a git-native
            // workspace directory, aggregating pass/fail. Reports are
            // per-flow (written next to each flow with a .<flow>.junit
            // suffix) so a CI reporter can glob them.
            var wsDir = pr.GetValue(workspaceDir);
            var wsId = pr.GetValue(workspaceId);

            // #583 — `--suite lint` is a different kind of run, not a variant
            // of a test run, so it is decided before anything test-shaped.
            var suiteName = pr.GetValue(suite);
            if (!string.IsNullOrEmpty(suiteName)
                && !string.Equals(suiteName, "tests", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(suiteName, "lint", StringComparison.OrdinalIgnoreCase))
                {
                    await stderr.WriteLineAsync(
                        $"bowire test: --suite '{suiteName}' is not one of tests, lint.").ConfigureAwait(false);
                    return 2;
                }
                if (!string.IsNullOrEmpty(wsDir) || !string.IsNullOrEmpty(wsId))
                {
                    // A workspace is a set of flows; lint reads an API surface.
                    // Accepting both would lint one and silently drop the other.
                    await stderr.WriteLineAsync(
                        "bowire test: --suite lint reads a snapshot or URL, not a workspace; drop --workspace / --workspace-id.")
                        .ConfigureAwait(false);
                    return 2;
                }
                // Lint's own default, not test's: `bowire lint` defaults to
                // "none" so adopting it never breaks a pipeline on the first
                // run (docs/features/lint.md), and with the Info-level naming
                // rules a real API has findings on day one. Only when neither
                // the flag nor Bowire:Test:FailOn says otherwise.
                var failOnGiven = pr.GetResult(failOn) is { Implicit: false }
                    || !string.IsNullOrEmpty(cfg["Bowire:Test:FailOn"]);
                if (!failOnGiven) options.FailOn = "never";
                options.Baseline = pr.GetValue(lintBaseline);
                return await LintSuiteRunner.RunAsync(options, stdout, stderr).ConfigureAwait(false);
            }
            if (!string.IsNullOrEmpty(pr.GetValue(lintBaseline)))
            {
                // A test run has nothing to compare; ignoring it would let a job
                // believe it gated on compatibility when it did not.
                await stderr.WriteLineAsync(
                    "bowire test: --baseline belongs to --suite lint.").ConfigureAwait(false);
                return 2;
            }
            if (!string.Equals(options.FailOn, "any", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.FailOn, "never", StringComparison.OrdinalIgnoreCase))
            {
                // A test run has no severities. Reading "high" as "any" would
                // gate on something the operator did not write.
                await stderr.WriteLineAsync(
                    $"bowire test: --fail-on '{options.FailOn}' is a lint severity; for a test run use 'any' or 'never', or add --suite lint.")
                    .ConfigureAwait(false);
                return 2;
            }

            if (options.FailFast && string.IsNullOrEmpty(options.DataPath))
            {
                await stderr.WriteLineAsync("bowire test: --fail-fast stops between iterations; it needs --data.").ConfigureAwait(false);
                return 2;
            }

            if (!string.IsNullOrEmpty(wsDir) && !string.IsNullOrEmpty(wsId))
            {
                // Both name a workspace and they can name different ones.
                // Picking one silently would run a suite the operator did
                // not ask for and report it as theirs.
                await stderr.WriteLineAsync(
                    "bowire test: --workspace and --workspace-id both name a workspace; pass one.")
                    .ConfigureAwait(false);
                return 2;
            }
            if (!string.IsNullOrEmpty(wsId))
            {
                // #365 — the id is what the workbench shows and what a person
                // would say out loud, so it is what CI can name. Which store
                // it is looked up in was settled at start-up.
                return await TestRunner.RunWorkspaceIdAsync(wsId, options, stdout, stderr).ConfigureAwait(false);
            }
            if (!string.IsNullOrEmpty(wsDir))
            {
                return await TestRunner.RunWorkspaceAsync(wsDir, options, stdout, stderr).ConfigureAwait(false);
            }

            return await TestRunner.RunAsync(options, stdout, stderr).ConfigureAwait(false);
        }
    }
}
