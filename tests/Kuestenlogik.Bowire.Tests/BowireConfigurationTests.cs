// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Kuestenlogik.Bowire.App.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Guards the bootstrap <see cref="IConfiguration"/> pipeline the CLI
/// uses before <c>WebApplication.CreateBuilder</c> runs: same source
/// priority, same keys reach plugin loading, appsettings.json support,
/// and the <c>--plugin-dir</c> / <c>BOWIRE_PLUGIN_DIR</c> translation.
/// </summary>
// Shared CWD lock with every other config-tests class — all three
// flip Directory.SetCurrentDirectory to isolate their appsettings.json
// fixture, so xUnit must serialise them to avoid cross-class races.
// See CwdSerialisedCollectionDefinition.cs for the matching
// DisableParallelization = true definition.
[Collection("CwdSerialised")]
public sealed class BowireConfigurationTests : IDisposable
{
    private readonly string _cwdBackup;
    private readonly string _tempDir;
    private readonly string? _envBackup;

    public BowireConfigurationTests()
    {
        _cwdBackup = Directory.GetCurrentDirectory();
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.SetCurrentDirectory(_tempDir);

        _envBackup = Environment.GetEnvironmentVariable("BOWIRE_PLUGIN_DIR");
        Environment.SetEnvironmentVariable("BOWIRE_PLUGIN_DIR", null);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_cwdBackup);
        Environment.SetEnvironmentVariable("BOWIRE_PLUGIN_DIR", _envBackup);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void NoSources_PluginDirIsNull()
    {
        var config = BowireConfiguration.Build([]);
        Assert.Null(BowireConfiguration.PluginDir(config));
    }

    [Fact]
    public void CliFlag_DashedName_BindsToConfigKey()
    {
        var config = BowireConfiguration.Build(["--plugin-dir", "/tmp/my-plugins"]);
        var dir = BowireConfiguration.PluginDir(config);
        Assert.Equal(Path.GetFullPath("/tmp/my-plugins"), dir);
    }

    [Fact]
    public void CliFlag_EqualsForm_Works()
    {
        var config = BowireConfiguration.Build(["--plugin-dir=/tmp/plugins-eq"]);
        var dir = BowireConfiguration.PluginDir(config);
        Assert.Equal(Path.GetFullPath("/tmp/plugins-eq"), dir);
    }

    [Fact]
    public void LegacyEnvVar_BindsToConfigKey()
    {
        var expected = Path.Combine(_tempDir, "env-plugins");
        Environment.SetEnvironmentVariable("BOWIRE_PLUGIN_DIR", expected);
        try
        {
            var config = BowireConfiguration.Build([]);
            var dir = BowireConfiguration.PluginDir(config);
            Assert.Equal(Path.GetFullPath(expected), dir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOWIRE_PLUGIN_DIR", null);
        }
    }

    [Fact]
    public void AppSettings_Json_ProvidesDefault()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"),
            """{ "Bowire": { "PluginDir": "from-appsettings" } }""");

        var config = BowireConfiguration.Build([]);
        Assert.Equal(Path.GetFullPath("from-appsettings"),
            BowireConfiguration.PluginDir(config));
    }

    [Fact]
    public void CliFlag_Overrides_EnvVar_Overrides_AppSettings()
    {
        // appsettings.json → env → CLI: CLI wins.
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"),
            """{ "Bowire": { "PluginDir": "from-json" } }""");
        Environment.SetEnvironmentVariable("BOWIRE_PLUGIN_DIR",
            Path.Combine(_tempDir, "from-env"));

        try
        {
            // With only JSON + env set, env wins.
            var envOnly = BowireConfiguration.Build([]);
            Assert.Equal(Path.GetFullPath(Path.Combine(_tempDir, "from-env")),
                BowireConfiguration.PluginDir(envOnly));

            // Add a CLI flag: CLI wins over env and over JSON.
            var cliOverrides = BowireConfiguration.Build(
                ["--plugin-dir", Path.Combine(_tempDir, "from-cli")]);
            Assert.Equal(Path.GetFullPath(Path.Combine(_tempDir, "from-cli")),
                BowireConfiguration.PluginDir(cliOverrides));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOWIRE_PLUGIN_DIR", null);
        }
    }

    [Fact]
    public void PerPluginSection_AvailableUnderBowirePlugins()
    {
        // Verifies the documented config convention — plugins bind their
        // section via IConfiguration.GetSection("Bowire:Plugins:<name>").
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"),
            """
            {
              "Bowire": {
                "Plugins": {
                  "Mqtt": { "BrokerPort": 1883, "Topic": "sensors/#" }
                }
              }
            }
            """);

        var config = BowireConfiguration.Build([]);
        var mqtt = config.GetSection("Bowire:Plugins:Mqtt");

        Assert.Equal("1883", mqtt["BrokerPort"]);
        Assert.Equal("sensors/#", mqtt["Topic"]);
    }

    [Fact]
    public void AddBowirePlugins_IConfigurationOverload_NoKey_IsNoOp()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        var result = services.AddBowirePlugins(config);
        Assert.Same(services, result);
    }

    [Fact]
    public void BuildBrowserUiOptions_RepeatedDisablePluginCommaSeparated_MergesIntoList()
    {
        // Both forms — repeated --disable-plugin and a single
        // comma-separated value — drop into DisabledPlugins without
        // duplicates.
        var config = new ConfigurationBuilder().Build();
        var options = BowireConfiguration.BuildBrowserUiOptions(
            config,
            ["--disable-plugin", "grpc",
             "--disable-plugin=signalr,mqtt",
             "--disable-plugin", "GRPC"]); // dup, case-insensitive

        Assert.Equal(3, options.DisabledPlugins.Count);
        Assert.Contains("grpc", options.DisabledPlugins, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("signalr", options.DisabledPlugins, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("mqtt", options.DisabledPlugins, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildBrowserUiOptions_UrlEqualsForm_AndShortFlag_PopulateServerUrls()
    {
        // -u=, --url=, and -u <value> all feed ExtractRepeatedUrls.
        var config = new ConfigurationBuilder().Build();
        var options = BowireConfiguration.BuildBrowserUiOptions(
            config,
            ["-u=http://a.local",
             "--url=http://b.local",
             "-u", "http://c.local"]);

        Assert.Equal(3, options.ServerUrls.Count);
        Assert.Equal("http://a.local", options.ServerUrls[0]);
        Assert.Equal("http://b.local", options.ServerUrls[1]);
        Assert.Equal("http://c.local", options.ServerUrls[2]);
        Assert.True(options.LockServerUrl);
    }

    [Fact]
    public void AddBowirePlugins_IConfigurationOverload_UsesBoundPluginDir()
    {
        // An empty but valid directory — the extension is a no-op since
        // there are no DLLs, but it must NOT throw on a configured path.
        var configDir = Path.Combine(_tempDir, "plugins");
        Directory.CreateDirectory(configDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:PluginDir"] = configDir
            })
            .Build();

        var services = new ServiceCollection();
        var result = services.AddBowirePlugins(config);
        Assert.Same(services, result);
    }
}
