// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Guards the browser-UI typed-options binding: port/title/URL(s)/
/// no-browser/enable-mcp-adapter read from the same shared config
/// stack and multi-value <c>--url</c> flags survive the round-trip
/// that switch-mapped single-value config keys can't handle on their
/// own.
/// </summary>
[Collection("CwdSerialised")]
public sealed class BrowserUiOptionsTests : IDisposable
{
    private readonly string _cwdBackup;
    private readonly string _tempDir;

    public BrowserUiOptionsTests()
    {
        _cwdBackup = Directory.GetCurrentDirectory();
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-uiopts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.SetCurrentDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_cwdBackup);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Defaults_WhenNothingConfigured()
    {
        var config = BowireConfiguration.Build([]);
        var ui = BowireConfiguration.BuildBrowserUiOptions(config, []);

        Assert.Equal(5080, ui.Port);
        Assert.Equal("Bowire", ui.Title);
        Assert.Null(ui.ServerUrl);
        Assert.Empty(ui.ServerUrls);
        Assert.False(ui.NoBrowser);
        Assert.False(ui.EnableMcpAdapter);
        Assert.False(ui.LockServerUrl);
    }

    [Fact]
    public void AppSettings_ProvidesAllDefaults()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"),
            """
            {
              "Bowire": {
                "Port": 7070,
                "Title": "Custom",
                "ServerUrl": "http://api.example.com",
                "NoBrowser": true,
                "EnableMcpAdapter": true
              }
            }
            """);

        var config = BowireConfiguration.Build([]);
        var ui = BowireConfiguration.BuildBrowserUiOptions(config, []);

        Assert.Equal(7070, ui.Port);
        Assert.Equal("Custom", ui.Title);
        Assert.Equal("http://api.example.com", ui.ServerUrl);
        Assert.True(ui.NoBrowser);
        Assert.True(ui.EnableMcpAdapter);
    }

    [Fact]
    public void CliFlag_Port_OverridesAppSettings()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"),
            """{ "Bowire": { "Port": 7070 } }""");

        var args = new[] { "--port", "8080" };
        var config = BowireConfiguration.Build(args);
        var ui = BowireConfiguration.BuildBrowserUiOptions(config, args);

        Assert.Equal(8080, ui.Port);
    }

    [Fact]
    public void CliFlag_Port_ShortForm_Works()
    {
        var args = new[] { "-p", "9090" };
        var config = BowireConfiguration.Build(args);
        var ui = BowireConfiguration.BuildBrowserUiOptions(config, args);

        Assert.Equal(9090, ui.Port);
    }

    [Fact]
    public void BareBooleanFlag_NoBrowser_BindsToTrue()
    {
        // --no-browser without a value should be expanded to
        // --no-browser true by the bootstrap so AddCommandLine doesn't
        // swallow the next positional arg.
        var args = new[] { "--no-browser" };
        var config = BowireConfiguration.Build(args);
        var ui = BowireConfiguration.BuildBrowserUiOptions(config, args);

        Assert.True(ui.NoBrowser);
    }

    [Fact]
    public void BareBooleanFlag_EnableMcpAdapter_BindsToTrue()
    {
        var args = new[] { "--enable-mcp-adapter" };
        var config = BowireConfiguration.Build(args);
        var ui = BowireConfiguration.BuildBrowserUiOptions(config, args);

        Assert.True(ui.EnableMcpAdapter);
    }

    [Fact]
    public void BareBooleanFlag_DoesNotSwallowFollowingArg()
    {
        // A bare --no-browser followed by --port 8080 must leave the
        // port intact — the expansion inserts "true" between the flag
        // and the next arg instead of consuming it.
        var args = new[] { "--no-browser", "--port", "8080" };
        var config = BowireConfiguration.Build(args);
        var ui = BowireConfiguration.BuildBrowserUiOptions(config, args);

        Assert.True(ui.NoBrowser);
        Assert.Equal(8080, ui.Port);
    }

    [Fact]
    public void SingleUrl_ViaCliFlag_PopulatesBothScalarAndList()
    {
        var args = new[] { "--url", "http://a.example" };
        var config = BowireConfiguration.Build(args);
        var ui = BowireConfiguration.BuildBrowserUiOptions(config, args);

        Assert.Equal("http://a.example", ui.ServerUrl);
        Assert.Single(ui.ServerUrls);
        Assert.Equal("http://a.example", ui.ServerUrls[0]);
        Assert.True(ui.LockServerUrl);
    }

    [Fact]
    public void MultipleUrls_ViaRepeatedCliFlag_CollectedInOrder()
    {
        // AddCommandLine's switch mapping would only keep the last --url,
        // so BuildBrowserUiOptions must collect the repeated flags
        // manually and merge them into ServerUrls.
        var args = new[] { "--url", "http://a", "--url", "http://b", "--url=http://c" };
        var config = BowireConfiguration.Build(args);
        var ui = BowireConfiguration.BuildBrowserUiOptions(config, args);

        Assert.Equal(["http://a", "http://b", "http://c"], ui.ServerUrls);
        // Primary is the first URL.
        Assert.Equal("http://a", ui.ServerUrl);
    }

    [Fact]
    public void UrlArray_InAppSettings_PopulatesServerUrls()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"),
            """
            {
              "Bowire": {
                "ServerUrls": ["http://x", "http://y"]
              }
            }
            """);

        var config = BowireConfiguration.Build([]);
        var ui = BowireConfiguration.BuildBrowserUiOptions(config, []);

        Assert.Equal(["http://x", "http://y"], ui.ServerUrls);
        Assert.Equal("http://x", ui.ServerUrl); // synced as primary
        Assert.True(ui.LockServerUrl);
    }

    [Fact]
    public void MultipleUrls_CliOverrides_AppSettingsArray()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"),
            """
            { "Bowire": { "ServerUrls": ["http://from-json"] } }
            """);

        var args = new[] { "--url", "http://from-cli-a", "--url", "http://from-cli-b" };
        var config = BowireConfiguration.Build(args);
        var ui = BowireConfiguration.BuildBrowserUiOptions(config, args);

        // CLI replaces — retyping --url is a full override, not an append.
        Assert.Equal(["http://from-cli-a", "http://from-cli-b"], ui.ServerUrls);
    }

    [Fact]
    public void PluginDir_FlowsThroughToOptions()
    {
        var target = Path.Combine(_tempDir, "pd-check");
        var args = new[] { "--plugin-dir", target };
        var config = BowireConfiguration.Build(args);
        var ui = BowireConfiguration.BuildBrowserUiOptions(config, args);

        Assert.Equal(Path.GetFullPath(target), ui.PluginDir);
    }
}
