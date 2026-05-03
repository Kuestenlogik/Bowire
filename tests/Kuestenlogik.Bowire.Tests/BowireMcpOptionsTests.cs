// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mcp;

namespace Kuestenlogik.Bowire.Tests;

public class BowireMcpOptionsTests
{
    [Fact]
    public void Default_Options_Have_Security_First_Defaults()
    {
        var options = new BowireMcpOptions();

        // Server identity
        Assert.Equal("bowire-mcp", options.ServerName);
        Assert.Equal("0.9.4", options.ServerVersion);

        // Allowlist starts empty and seeding from environments is on by default
        Assert.NotNull(options.AllowedServerUrls);
        Assert.Empty(options.AllowedServerUrls);
        Assert.True(options.LoadAllowlistFromEnvironments);

        // Arbitrary URLs blocked by default
        Assert.False(options.AllowArbitraryUrls);

        // Subscribe window defaults
        Assert.Equal(30_000, options.MaxSubscribeMs);
        Assert.Equal(5_000, options.DefaultSubscribeMs);
        Assert.Equal(200, options.MaxSubscribeFrames);
    }

    [Fact]
    public void Properties_Can_Be_Customized()
    {
        var options = new BowireMcpOptions
        {
            ServerName = "custom",
            ServerVersion = "2.0.0",
            LoadAllowlistFromEnvironments = false,
            AllowArbitraryUrls = true,
            MaxSubscribeMs = 60_000,
            DefaultSubscribeMs = 1_000,
            MaxSubscribeFrames = 50,
        };

        Assert.Equal("custom", options.ServerName);
        Assert.Equal("2.0.0", options.ServerVersion);
        Assert.False(options.LoadAllowlistFromEnvironments);
        Assert.True(options.AllowArbitraryUrls);
        Assert.Equal(60_000, options.MaxSubscribeMs);
        Assert.Equal(1_000, options.DefaultSubscribeMs);
        Assert.Equal(50, options.MaxSubscribeFrames);
    }

    [Fact]
    public void AllowedServerUrls_Is_Mutable_Collection()
    {
        var options = new BowireMcpOptions();

        options.AllowedServerUrls.Add("https://api.example.com");
        options.AllowedServerUrls.Add("https://other.example.com");

        Assert.Equal(2, options.AllowedServerUrls.Count);
        Assert.Contains("https://api.example.com", options.AllowedServerUrls);
        Assert.Contains("https://other.example.com", options.AllowedServerUrls);
    }
}
