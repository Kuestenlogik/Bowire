// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.App.Configuration;

// Force the console to UTF-8 so subcommand output (list / describe / call,
// the discovery-result printers, the mcp-serve handshake) renders non-
// ASCII characters correctly on Windows. The default OutputEncoding on
// Windows is the legacy active code page (1252 / 850 / …), which
// mojibakes multi-byte UTF-8 strings — every service name, method name,
// summary, or description coming from a discovered API can carry
// non-ASCII (em-dashes, German umlauts, Asian scripts, emoji in
// summaries). InputEncoding too, so piped JSON / YAML stays intact.
//
// Wrapped in try/catch because some test/CI hosts redirect console
// streams in ways that reject SetEncoding — the tool keeps booting.
try
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.InputEncoding = Encoding.UTF8;
}
catch (IOException) { /* console handle not encoding-settable here */ }

// Bootstrap IConfiguration once: appsettings.json -> BOWIRE_* env ->
// --flag overrides. Plugin loading + every subcommand's defaults read
// from this same instance.
var bootstrapConfig = BowireConfiguration.Build(args);
var pluginDir = BowireConfiguration.PluginDir(bootstrapConfig) ?? "";

// Load plugin assemblies before subcommand dispatch — plugins must be
// in the AppDomain before any DiscoverProtocolRegistry pass runs
// (browser UI, mcp serve, list/describe/call, etc.).
PluginManager.LoadPlugins(pluginDir);

// All subcommand routing + the default browser-UI action are declared
// in BowireCli using System.CommandLine 2.0.7. Auto-generated help,
// validation, and tab-completion for every subcommand land here for
// free; the per-subcommand handlers in CliHandler / MockCommand /
// McpServeCommand / TestRunner / PluginManager keep their existing
// implementations (called via typed-args in mcp serve, pass-through
// in the others — see BowireCli for the migration boundary).
return await BowireCli.RunAsync(args, bootstrapConfig, pluginDir);
