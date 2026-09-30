// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The startup banner says when targets are locked and, without a listed
/// identity provider, how to allow one — so an external OAuth provider does
/// not first show up as a 403 on the first token call.
/// </summary>
public sealed class BrowserUiHostTargetLockNoticeTests
{
    [Fact]
    public void Unlocked_host_prints_nothing()
    {
        Assert.Empty(BrowserUiHost.TargetLockNotice(new BrowserUiOptions()));
    }

    [Fact]
    public void Locked_host_without_auth_urls_names_the_flag()
    {
        var ui = new BrowserUiOptions();
        ui.ServerUrls.Add("https://api.example.com");

        var lines = BrowserUiHost.TargetLockNotice(ui);

        Assert.Equal(2, lines.Count);
        Assert.Contains("403", lines[0], StringComparison.Ordinal);
        Assert.Contains("--allowed-auth-url", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Locked_host_with_auth_urls_drops_the_hint()
    {
        var ui = new BrowserUiOptions();
        ui.ServerUrls.Add("https://api.example.com");
        ui.AllowedAuthUrls.Add("https://login.example.com");

        var lines = BrowserUiHost.TargetLockNotice(ui);

        Assert.Single(lines);
        Assert.DoesNotContain("--allowed-auth-url", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AllowedServerUrls_alone_also_announce_the_restriction()
    {
        var ui = new BrowserUiOptions();
        ui.AllowedServerUrls.Add("https://api.example.com");

        Assert.NotEmpty(BrowserUiHost.TargetLockNotice(ui));
    }
}
