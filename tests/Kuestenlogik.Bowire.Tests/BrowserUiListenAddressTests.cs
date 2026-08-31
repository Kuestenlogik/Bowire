// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The address-precedence rules from #634.
/// </summary>
/// <remarks>
/// <para>
/// The defect these pin down was invisible from the source: one unconditional
/// <c>UseUrls</c> call, which sits above every other address source in ASP.NET
/// and therefore discarded <c>ASPNETCORE_URLS</c> and <c>Kestrel:Endpoints</c>
/// without a word. An operator configured HTTPS and got plaintext on 5080.
/// </para>
/// <para>
/// Pure functions, so the whole precedence table is one cheap test each rather
/// than a server per case.
/// </para>
/// </remarks>
public class BrowserUiListenAddressTests
{
    [Fact]
    public void ResolveListenAddress_NothingConfiguredAnywhere_KeepsTheDefault()
    {
        var (urls, note) = BrowserUiHost.ResolveListenAddress(
            portExplicit: false, port: 5080, platformConfigured: false);

        // A plain `bowire` has to behave exactly as it did before #634.
        Assert.Equal("http://localhost:5080", urls);
        Assert.Null(note);
    }

    [Fact]
    public void ResolveListenAddress_PlatformConfiguredAndNoPortPassed_LeavesItAlone()
    {
        var (urls, note) = BrowserUiHost.ResolveListenAddress(
            portExplicit: false, port: 5080, platformConfigured: true);

        // The whole point: null means "do not call UseUrls", which is what
        // lets a configured HTTPS endpoint — certificate and all — survive.
        Assert.Null(urls);
        Assert.Null(note);
    }

    [Fact]
    public void ResolveListenAddress_ExplicitPort_StillWins()
    {
        var (urls, _) = BrowserUiHost.ResolveListenAddress(
            portExplicit: true, port: 7070, platformConfigured: true);

        // --port is a command-line argument and outranks environment and
        // appsettings. The VS Code extension passes it with --port-file and
        // has to keep winning.
        Assert.Equal("http://localhost:7070", urls);
    }

    [Fact]
    public void ResolveListenAddress_ExplicitPortOverAConfiguredAddress_SaysSo()
    {
        var (_, note) = BrowserUiHost.ResolveListenAddress(
            portExplicit: true, port: 7070, platformConfigured: true);

        // Overriding is allowed; overriding in silence is the bug. The note
        // has to name both the winner and the way out.
        Assert.NotNull(note);
        Assert.Contains("7070", note, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_URLS", note, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveListenAddress_ExplicitPortWithNothingConfigured_SaysNothing()
    {
        var (urls, note) = BrowserUiHost.ResolveListenAddress(
            portExplicit: true, port: 7070, platformConfigured: false);

        Assert.Equal("http://localhost:7070", urls);
        // Nothing was overridden, so there is nothing to report. A message on
        // every start is a message nobody reads.
        Assert.Null(note);
    }

    [Fact]
    public void ResolveListenAddress_PortZero_NamesAConcreteLoopbackAddress()
    {
        var (urls, _) = BrowserUiHost.ResolveListenAddress(
            portExplicit: true, port: 0, platformConfigured: false);

        // Kestrel refuses to bind "localhost" dynamically — "Dynamic port
        // binding is not supported when binding to localhost".
        Assert.Equal("http://127.0.0.1:0", urls);
    }

    [Theory]
    [InlineData("urls", "https://localhost:5001")]
    [InlineData("http_ports", "5080")]
    [InlineData("https_ports", "5001")]
    [InlineData("Kestrel:Endpoints:Https:Url", "https://localhost:5001")]
    public void PlatformAddressConfigured_RecognisesEveryKeyAspNetItselfReads(string key, string value)
    {
        var configuration = Configure((key, value));

        Assert.True(BrowserUiHost.PlatformAddressConfigured(configuration));
    }

    [Fact]
    public void PlatformAddressConfigured_EmptyConfiguration_IsFalse()
    {
        Assert.False(BrowserUiHost.PlatformAddressConfigured(Configure()));
    }

    [Fact]
    public void PlatformAddressConfigured_BowiresOwnUrlFlag_DoesNotCount()
    {
        // Bowire's --url names the services to probe. Reading it as a listen
        // address would stop the tool binding anything at all.
        var configuration = Configure(("Bowire:ServerUrls:0", "https://api.example.com"));

        Assert.False(BrowserUiHost.PlatformAddressConfigured(configuration));
    }

    [Theory]
    [InlineData("https://localhost:5001", null, "https")]
    [InlineData("http://localhost:5080", null, "http")]
    [InlineData(null, "5001", "https")]
    [InlineData(null, null, "http")]
    public void ConfiguredScheme_FollowsWhatTheConfigurationAsksFor(
        string? urls, string? httpsPorts, string expected)
    {
        var configuration = Configure(("urls", urls), ("https_ports", httpsPorts));

        Assert.Equal(expected, BrowserUiHost.ConfiguredScheme(configuration));
    }

    private static IConfiguration Configure(params (string Key, string? Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries
                .Where(e => e.Value is not null)
                .Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();
}
