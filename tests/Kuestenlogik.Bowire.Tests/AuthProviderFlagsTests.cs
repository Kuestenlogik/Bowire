// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Tests.Plugins;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Provider-namespaced auth flags reach the configuration the provider reads
/// (#818). Before, <c>--auth-oidc-*</c> was refused by the parser, and
/// <c>--auth-provider</c> itself never reached the web host — the workbench
/// started open.
/// </summary>
public sealed class AuthProviderFlagsTests
{
    [Fact]
    public void Settings_Become_Keys_Under_The_Providers_Section()
    {
        var (remaining, settings) = AuthProviderFlags.Extract(
            ["--port", "5080", "--auth-provider", "oidc", "--auth-oidc-authority", "https://login.example.com/", "--auth-oidc-client-id=bowire"]);

        Assert.Equal(["--port", "5080", "--auth-provider", "oidc"], remaining);
        Assert.Equal("https://login.example.com/", settings["Bowire:Auth:Oidc:Authority"]);
        Assert.Equal("bowire", settings["Bowire:Auth:Oidc:ClientId"]);
    }

    [Fact]
    public void A_Name_Value_Pair_Is_An_Entry_Of_A_Map_Setting_And_May_Repeat()
    {
        var (_, settings) = AuthProviderFlags.Extract(
            ["--auth-oidc-required-claim", "groups=bowire-users", "--auth-oidc-required-claim", "tenant_id=acme"]);

        Assert.Equal("bowire-users", settings["Bowire:Auth:Oidc:RequiredClaim:groups"]);
        Assert.Equal("acme", settings["Bowire:Auth:Oidc:RequiredClaim:tenant_id"]);
    }

    [Fact]
    public void A_Url_With_A_Query_Is_Not_Split_At_Its_Equals_Sign()
    {
        var (_, settings) = AuthProviderFlags.Extract(["--auth-oidc-authority", "https://login.example.com/?p=b2c_1"]);
        Assert.Equal("https://login.example.com/?p=b2c_1", settings["Bowire:Auth:Oidc:Authority"]);
    }

    [Fact]
    public void A_Flag_Without_A_Setting_Name_Is_Left_For_The_Parser()
    {
        var (remaining, settings) = AuthProviderFlags.Extract(["--auth-oidc", "x"]);
        Assert.Equal(["--auth-oidc", "x"], remaining);
        Assert.Empty(settings);
    }

    [Fact]
    public void The_Web_Host_Configuration_Carries_The_Provider_And_Its_Settings()
    {
        var builder = new ConfigurationBuilder();
        BrowserUiHost.AddHostCommandLine(builder,
            ["--no-browser", "--auth-provider", "oidc", "--auth-oidc-authority", "https://login.example.com/", "--auth-oidc-required-claim", "groups=bowire-users"]);
        var config = builder.Build();

        Assert.Equal("oidc", config["Bowire:Auth:ProviderId"]);
        Assert.Equal("https://login.example.com/", config["Bowire:Auth:Oidc:Authority"]);
        Assert.Equal("bowire-users", config["Bowire:Auth:Oidc:RequiredClaim:groups"]);
    }

    [Fact]
    public async Task The_Parser_Accepts_Provider_Settings()
    {
        using var outW = new StringWriter();
        using var errW = new StringWriter();
        var code = await BowireCli.RunAsync(
            ["--auth-oidc-authority", "https://login.example.com/", "--help"],
            new ConfigurationBuilder().Build(), TestPluginLoaders.None(), outW, errW,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.DoesNotContain("--auth-oidc-authority", errW.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plugin", "list")]
    [InlineData("plugin", "install", "--help")]
    public async Task The_Plugin_Verbs_Accept_The_Plugin_Dir_Their_Usage_Names(params string[] verb)
    {
        using var outW = new StringWriter();
        using var errW = new StringWriter();
        var dir = Path.Combine(Path.GetTempPath(), "bowire-plugin-dir-" + Guid.NewGuid().ToString("N"));
        var code = await BowireCli.RunAsync(
            [.. verb, "--plugin-dir", dir],
            new ConfigurationBuilder().Build(), TestPluginLoaders.None(), outW, errW,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("--plugin-dir", errW.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, code);
    }
}
