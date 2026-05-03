// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire;

namespace Kuestenlogik.Bowire.Tests;

public class BowireOptionsTests
{
    [Fact]
    public void Default_Options_Have_Expected_Values()
    {
        var options = new BowireOptions();

        Assert.Equal("Bowire", options.Title);
        Assert.Equal("gRPC API Browser", options.Description);
        Assert.Equal(BowireTheme.Dark, options.Theme);
        Assert.Equal("bowire", options.RoutePrefix);
        Assert.Null(options.ServerUrl);
        Assert.False(options.ShowInternalServices);
    }

    [Fact]
    public void Options_Can_Be_Customized()
    {
        var options = new BowireOptions
        {
            Title = "My API",
            Description = "Custom gRPC Browser",
            Theme = BowireTheme.Light,
            RoutePrefix = "grpc",
            ServerUrl = "https://localhost:5001",
            ShowInternalServices = true
        };

        Assert.Equal("My API", options.Title);
        Assert.Equal("Custom gRPC Browser", options.Description);
        Assert.Equal(BowireTheme.Light, options.Theme);
        Assert.Equal("grpc", options.RoutePrefix);
        Assert.Equal("https://localhost:5001", options.ServerUrl);
        Assert.True(options.ShowInternalServices);
    }

    [Fact]
    public void Theme_Enum_Has_Expected_Values()
    {
        Assert.Equal(0, (int)BowireTheme.Dark);
        Assert.Equal(1, (int)BowireTheme.Light);
    }
}
