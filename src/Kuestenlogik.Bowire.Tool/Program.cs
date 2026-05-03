// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.App.Configuration;

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
