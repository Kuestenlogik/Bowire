// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.App.Configuration;

/// <summary>
/// Shared <see cref="IConfiguration"/> bootstrap for the <c>bowire</c> CLI.
/// Built once at the very start of <c>Program.cs</c>, before plugin loading
/// (which has to happen before ASP.NET's own <c>WebApplication.CreateBuilder</c>
/// runs its reflection pass), so the same config keys reach plugin loading
/// in browser-UI mode <i>and</i> the subcommands (<c>plugin</c>,
/// <c>mock</c>, <c>test</c>, …).
/// </summary>
/// <remarks>
/// <para>
/// Config source priority (highest wins) follows standard .NET conventions:
/// </para>
/// <list type="number">
///   <item>Command-line args (<c>--plugin-dir</c>, <c>--port</c>, …)</item>
///   <item>Legacy env-var translation (<c>BOWIRE_PLUGIN_DIR</c> → <c>Bowire:PluginDir</c>)</item>
///   <item>Prefixed environment variables (<c>BOWIRE_</c> prefix stripped; <c>__</c> → <c>:</c>)</item>
///   <item><c>appsettings.{Environment}.json</c> (optional)</item>
///   <item><c>appsettings.json</c> (optional)</item>
/// </list>
/// <para>
/// Per-plugin config follows the convention <c>Bowire:Plugins:&lt;PluginName&gt;</c>.
/// Plugin authors bind their section the standard .NET way inside
/// <c>IBowireProtocolServices.ConfigureServices</c>:
/// </para>
/// <code>
/// services.AddOptions&lt;MqttPluginOptions&gt;()
///         .BindConfiguration("Bowire:Plugins:Mqtt");
/// </code>
/// <para>
/// This works because <c>WebApplication.CreateBuilder(args)</c> registers
/// <see cref="IConfiguration"/> as a DI singleton, and <c>BindConfiguration</c>
/// resolves it lazily at options-resolution time.
/// </para>
/// </remarks>
internal static class BowireConfiguration
{
    /// <summary>Environment variable to select <c>appsettings.{Environment}.json</c>.</summary>
    public const string EnvironmentVarName = "BOWIRE_ENVIRONMENT";

    /// <summary>
    /// CLI flag → config-key mappings. Keeps the dashed flag names users
    /// already type (<c>--plugin-dir</c>) while binding them to idiomatic
    /// colon-separated config keys (<c>Bowire:PluginDir</c>). Adding a new
    /// flag is one line here — no per-command arg-parser changes.
    /// </summary>
    private static readonly Dictionary<string, string> s_switchMappings = new(StringComparer.Ordinal)
    {
        ["--plugin-dir"] = "Bowire:PluginDir",
        ["--port"] = "Bowire:Port",
        ["-p"] = "Bowire:Port",
        ["--host"] = "Bowire:Host",
        ["--title"] = "Bowire:Title",
        ["--url"] = "Bowire:ServerUrl",
        ["-u"] = "Bowire:ServerUrl",
        ["--no-browser"] = "Bowire:NoBrowser",
        ["--enable-mcp-adapter"] = "Bowire:EnableMcpAdapter",
        // Subcommand-specific flags that also appear in the top-level
        // pass (because Program.cs builds the bootstrap config from the
        // whole arg list to resolve --plugin-dir). Bind them to
        // throwaway keys so AddCommandLine doesn't reject single-dash
        // short switches as unknown. The real bindings happen inside
        // the subcommand configs.
        ["--verbose"] = "Bowire:_Verbose",
        ["-v"] = "Bowire:_Verbose",
        ["-plaintext"] = "Bowire:_Plaintext",
        ["--plaintext"] = "Bowire:_Plaintext",
        ["--compact"] = "Bowire:_Compact",
        ["--no-watch"] = "Bowire:_NoWatch",
        ["--stateful"] = "Bowire:_Stateful",
        ["--stateful-once"] = "Bowire:_StatefulOnce",
        ["--loop"] = "Bowire:_Loop"
    };

    /// <summary>
    /// Flags that are bare toggles on the CLI (<c>--no-browser</c>)
    /// rather than key=value pairs. <see cref="ExpandBooleanFlags"/>
    /// rewrites them into the <c>--flag true</c> form so
    /// <c>AddCommandLine</c> can parse them — otherwise a bare flag
    /// would be interpreted as a key whose value is the next positional
    /// arg (or skipped entirely).
    /// </summary>
    /// <remarks>
    /// The union of every subcommand's bare-boolean flags so the
    /// top-level config builder doesn't misinterpret a subcommand flag
    /// (e.g. <c>plugin list --verbose --plugin-dir foo</c>). Each
    /// subcommand's builder still uses its own narrow set; this one is
    /// only for the pre-subcommand pass.
    /// </remarks>
    private static readonly HashSet<string> s_booleanFlags = new(StringComparer.Ordinal)
    {
        "--no-browser",
        "--enable-mcp-adapter",
        "--no-watch",
        "--stateful",
        "--stateful-once",
        "--loop",
        "-plaintext",
        "--plaintext",
        "--verbose",
        "-v",
        "--compact"
    };

    /// <summary>
    /// Env-var → config-key translation for the short-form names Bowire
    /// already documented before the config stack existed. New options
    /// should use the standard <c>BOWIRE_Bowire__KeyName</c> shape
    /// (handled by <c>AddEnvironmentVariables("BOWIRE_")</c> directly,
    /// which strips the prefix and turns <c>__</c> into <c>:</c>).
    /// </summary>
    private static readonly Dictionary<string, string> s_legacyEnvVars = new(StringComparer.Ordinal)
    {
        ["BOWIRE_PLUGIN_DIR"] = "Bowire:PluginDir"
    };

    /// <summary>
    /// Build the shared configuration. Pass the same <paramref name="args"/>
    /// into any downstream <c>CreateBuilder(args)</c> so the full ASP.NET
    /// config stack sees the same CLI overrides and nothing contradicts.
    /// </summary>
    public static IConfiguration Build(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return BuildCore(args, s_switchMappings, s_booleanFlags);
    }

    /// <summary>
    /// Shared builder used by the top-level <see cref="Build"/> and by
    /// every subcommand's config (each passes its own switch-mapping +
    /// boolean-flag set). All four config layers (appsettings, env,
    /// legacy env, CLI) read from the same sources — only the final
    /// AddCommandLine mapping shifts, so <c>bowire --port 5080</c> hits
    /// <c>Bowire:Port</c> while <c>bowire mock --port 6000</c> hits
    /// <c>Bowire:Mock:Port</c>.
    /// </summary>
    private static IConfiguration BuildCore(
        string[] args,
        IDictionary<string, string> switchMappings,
        HashSet<string> booleanFlags)
    {
        // AddCommandLine treats `--no-browser` (no value) ambiguously; an
        // explicit `true` behind the bare flag makes the binding obvious.
        var expandedArgs = ExpandBooleanFlags(args, booleanFlags);

        var environment =
            Environment.GetEnvironmentVariable(EnvironmentVarName)
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";

        var legacyEnvBindings = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (envName, configKey) in s_legacyEnvVars)
        {
            var value = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrEmpty(value))
            {
                legacyEnvBindings[configKey] = value;
            }
        }

        // The base path drives where appsettings.json is looked up. Prefer
        // the current directory (where the user launched `bowire` from)
        // so project-local config works without an absolute path; fall
        // back to the tool's install directory so the dotnet-tool install
        // can still ship defaults alongside the binary.
        var cwd = Directory.GetCurrentDirectory();

        var builder = new ConfigurationBuilder()
            .SetBasePath(cwd)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "BOWIRE_")
            .AddInMemoryCollection(legacyEnvBindings)
            .AddCommandLine(expandedArgs, switchMappings);

        return builder.Build();
    }

    /// <summary>
    /// Bind the browser-UI configuration section and merge in any
    /// multi-value <c>--url</c> flags from the command line. Multi-URL
    /// is the one shape AddCommandLine's switch mappings can't handle
    /// natively (last-wins semantics), so repeated flags get collected
    /// here and appended to <see cref="BrowserUiOptions.ServerUrls"/>
    /// on top of whatever appsettings.json provided.
    /// </summary>
    public static BrowserUiOptions BuildBrowserUiOptions(IConfiguration configuration, string[] args)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(args);

        var options = new BrowserUiOptions();
        configuration.GetSection("Bowire").Bind(options);

        var cliUrls = ExtractRepeatedUrls(args);
        if (cliUrls.Count > 0)
        {
            // CLI wins — replace appsettings' list entirely so users
            // can override a config-file default by retyping --url.
            options.ServerUrls.Clear();
            options.ServerUrls.AddRange(cliUrls);
        }

        // Keep ServerUrl and ServerUrls in sync. When the list is
        // non-empty the primary is always its first element — this
        // matters most when AddCommandLine's switch mapping picked the
        // *last* --url (last-wins scalar semantics) but the manual
        // repeated-flag extraction captured the full list in order.
        if (options.ServerUrls.Count > 0)
        {
            options.ServerUrl = options.ServerUrls[0];
        }
        else if (!string.IsNullOrEmpty(options.ServerUrl))
        {
            options.ServerUrls.Add(options.ServerUrl);
        }

        options.PluginDir = PluginDir(configuration);
        return options;
    }

    // Walk the raw args once and pull out every --url / -u value. Kept
    // separate from AddCommandLine so the multi-value shape survives —
    // switch mappings overwrite, which is correct for scalar knobs but
    // wrong for a list that users expect to grow with each flag.
    private static List<string> ExtractRepeatedUrls(string[] args)
    {
        var urls = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--url=", StringComparison.Ordinal))
            {
                urls.Add(a["--url=".Length..]);
            }
            else if (a.StartsWith("-u=", StringComparison.Ordinal))
            {
                urls.Add(a["-u=".Length..]);
            }
            else if ((a == "--url" || a == "-u") && i + 1 < args.Length)
            {
                urls.Add(args[++i]);
            }
        }
        return urls;
    }

    private static string[] ExpandBooleanFlags(string[] args, HashSet<string> booleanFlags)
    {
        // Hot path: if none of the known boolean flags appear as a bare
        // token, return the original array without allocating.
        var needsExpansion = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (booleanFlags.Contains(args[i]))
            {
                // Only expand when the flag is truly bare — if the next
                // arg already looks like a value (not starting with '-'),
                // the user's intent is ambiguous, so leave the pair alone
                // and let AddCommandLine interpret it.
                if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                {
                    needsExpansion = true;
                    break;
                }
            }
        }
        if (!needsExpansion) return args;

        var expanded = new List<string>(args.Length + 4);
        for (var i = 0; i < args.Length; i++)
        {
            expanded.Add(args[i]);
            if (booleanFlags.Contains(args[i]) &&
                (i + 1 >= args.Length || args[i + 1].StartsWith('-')))
            {
                expanded.Add("true");
            }
        }
        return expanded.ToArray();
    }

    /// <summary>Resolve the active plugin directory.</summary>
    /// <remarks>
    /// Returns the absolute path when a value is configured, or
    /// <c>null</c> when none of the layers set <c>Bowire:PluginDir</c>
    /// — in which case <see cref="PluginManager.ResolvePluginDir"/> falls
    /// back to the default <c>~/.bowire/plugins/</c>.
    /// </remarks>
    public static string? PluginDir(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration["Bowire:PluginDir"];
        return string.IsNullOrWhiteSpace(raw) ? null : Path.GetFullPath(raw);
    }
}
