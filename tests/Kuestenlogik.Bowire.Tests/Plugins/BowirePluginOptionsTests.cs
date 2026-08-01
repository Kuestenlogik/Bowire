// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Plugins;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// The precedence chain of <see cref="BowirePluginOptions"/> (#546).
/// </summary>
/// <remarks>
/// The point of the type is that there is exactly one implementation of
/// "which directory holds the plugins". These tests pin the ranking, and
/// the first one pins the property that matters most: an explicitly
/// supplied directory is decided before the environment is ever consulted,
/// so a test can construct plugin management with no ambient state at all.
/// </remarks>
[Collection("CwdSerialised")]
public sealed class BowirePluginOptionsTests : IDisposable
{
    private readonly string? _envBackup;

    public BowirePluginOptionsTests()
    {
        _envBackup = Environment.GetEnvironmentVariable(BowirePluginOptions.EnvVarName);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BowirePluginOptions.EnvVarName, _envBackup);
        GC.SuppressFinalize(this);
    }

    private static IConfiguration ConfigWith(string? pluginDir)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BowirePluginOptions.ConfigurationKey] = pluginDir,
            })
            .Build();

    [Fact]
    public void Resolve_ExplicitDirectory_IgnoresEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(
            BowirePluginOptions.EnvVarName, Path.Combine(Path.GetTempPath(), "poison-env"));

        var wanted = Path.Combine(Path.GetTempPath(), "bowire-opt-" + Guid.NewGuid().ToString("N"));
        var options = BowirePluginOptions.Resolve(wanted);

        Assert.Equal(Path.GetFullPath(wanted), options.PluginDirectory);
    }

    [Fact]
    public void Resolve_Configuration_BeatsEnvironmentVariable()
    {
        // BOWIRE_PLUGIN_DIR reaches a configured caller through the
        // configuration stack, so the direct read must stay switched off
        // whenever a configuration is supplied — otherwise the variable
        // would outrank the --plugin-dir flag that landed on the same key.
        Environment.SetEnvironmentVariable(
            BowirePluginOptions.EnvVarName, Path.Combine(Path.GetTempPath(), "from-env"));

        var fromConfig = Path.Combine(Path.GetTempPath(), "from-config");
        var options = BowirePluginOptions.Resolve(configuration: ConfigWith(fromConfig));

        Assert.Equal(Path.GetFullPath(fromConfig), options.PluginDirectory);
    }

    [Fact]
    public void Resolve_EnvironmentVariable_UsedWhenNoConfigurationSupplied()
    {
        var fromEnv = Path.Combine(Path.GetTempPath(), "bowire-env-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(BowirePluginOptions.EnvVarName, fromEnv);

        var options = BowirePluginOptions.Resolve();

        Assert.Equal(Path.GetFullPath(fromEnv), options.PluginDirectory);
    }

    [Fact]
    public void Resolve_BlankConfigurationValue_FallsThroughToDefault()
    {
        // A configuration that answers with whitespace is answering "unset".
        // Treating it as a directory would resolve to the working directory.
        Environment.SetEnvironmentVariable(BowirePluginOptions.EnvVarName, null);

        var options = BowirePluginOptions.Resolve(configuration: ConfigWith("   "));

        Assert.Equal(BowirePluginOptions.DefaultDirectory, options.PluginDirectory);
    }

    [Fact]
    public void Resolve_NothingConfigured_FallsBackToDefaultDirectory()
    {
        Environment.SetEnvironmentVariable(BowirePluginOptions.EnvVarName, null);

        var options = BowirePluginOptions.Resolve();

        Assert.Equal(BowirePluginOptions.DefaultDirectory, options.PluginDirectory);
    }

    [Fact]
    public void Resolve_RelativePath_ResolvesAgainstSuppliedBasePath()
    {
        // Passing a base path is how a test keeps off the process-global
        // working directory: Path.GetFullPath would otherwise answer
        // differently depending on who ran last.
        var basePath = Path.Combine(Path.GetTempPath(), "bowire-base-" + Guid.NewGuid().ToString("N"));

        var options = BowirePluginOptions.Resolve("plugins", basePath: basePath);

        Assert.Equal(Path.GetFullPath(Path.Combine(basePath, "plugins")), options.PluginDirectory);
    }

    [Fact]
    public void ConstructedDirectly_ReadsNothingAmbient()
    {
        // Acceptance criterion 1 of #546, in one line: plugin management
        // configured with an explicit directory and no environment variable.
        Environment.SetEnvironmentVariable(
            BowirePluginOptions.EnvVarName, Path.Combine(Path.GetTempPath(), "poison-env"));

        var wanted = Path.Combine(Path.GetTempPath(), "bowire-direct");
        var options = new BowirePluginOptions { PluginDirectory = wanted };

        Assert.Equal(wanted, options.PluginDirectory);
    }
}
