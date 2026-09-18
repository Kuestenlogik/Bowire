// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.App.Plugins;
using Kuestenlogik.Bowire.Projects;

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

// Settle where this process stores things, before anything reads or writes
// it. BOWIRE_DATA_DIR first, then a project manifest that opted into
// .bowire/ beside the code, then the machine-wide ~/.bowire.
//
// This used to run only inside AddBowire(), i.e. only when a host was being
// built. Every bare subcommand therefore resolved through the default store
// and read ~/.bowire whatever the variable or the manifest said — so a CI
// job that set BOWIRE_DATA_DIR to isolate itself still read, and wrote, the
// developer's own storage. The plugin directory was moved by #643; the
// workspace-scoped artifacts behind it — collections, recordings, flows,
// environments, the workspace inventory — were not.
//
// Before the plugin loader on purpose, so a plugin's own settings resolve
// against the same root as everything else.
BowireStorageRoot.Apply();

// Bootstrap IConfiguration once: appsettings.json -> BOWIRE_* env ->
// --flag overrides. Plugin loading + every subcommand's defaults read
// from this same instance.
var bootstrapConfig = BowireConfiguration.Build(args);

// The composition root for plugin management. Everything downstream —
// the browser UI, `bowire mock`, the plugin verbs — receives this one
// loader rather than reaching for process-global state, so "which
// plugins are loaded" has a single owner (#546).
var plugins = new BowirePluginLoader(
    BowirePluginOptions.Resolve(configuration: bootstrapConfig));

// Load plugin assemblies before subcommand dispatch — plugins must be
// in the AppDomain before any DiscoverProtocolRegistry pass runs
// (browser UI, mcp serve, list/describe/call, etc.).
//
// EXCEPT the `plugin` management group (install / uninstall / update /
// download / list / inspect): those operate on the plugin *directory*
// through PluginManager, never on loaded protocol instances. Eager-loading
// a plugin assembly here memory-maps its DLL — Windows then refuses the
// delete a subsequent `plugin uninstall` / `update` needs, so the verb
// that is meant to remove a plugin is blocked by having just loaded it.
// Skipping the load for this group keeps those verbs able to touch their
// own files (and drops the reflection-scan warnings from their output).
if (!BowireCli.IsPluginManagementCommand(args))
{
    plugins.Load();
}

// All subcommand routing + the default browser-UI action are declared
// in BowireCli using System.CommandLine 2.0.7. Auto-generated help,
// validation, and tab-completion for every subcommand land here for
// free; the per-subcommand handlers in CliHandler / MockCommand /
// McpServeCommand / TestRunner / PluginManager keep their existing
// implementations (called via typed-args in mcp serve, pass-through
// in the others — see BowireCli for the migration boundary).
return await BowireCli.RunAsync(args, bootstrapConfig, plugins);
