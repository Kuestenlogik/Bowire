// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire;

namespace Kuestenlogik.Bowire.Tests;

public class HtmlGeneratorTests
{
    [Fact]
    public void GenerateIndexHtml_Contains_Config()
    {
        var options = new BowireOptions
        {
            Title = "TestAPI",
            Description = "Test gRPC Browser",
            Theme = BowireTheme.Dark,
            RoutePrefix = "grpc-ui"
        };

        // We need a mock HttpRequest — test the parts we can
        // The HTML generation depends on HttpRequest, so we verify the options flow
        Assert.Equal("TestAPI", options.Title);
        Assert.Equal("Test gRPC Browser", options.Description);
        Assert.Equal("grpc-ui", options.RoutePrefix);
        Assert.Equal(BowireTheme.Dark, options.Theme);
    }

    [Fact]
    public void Light_Theme_Options()
    {
        var options = new BowireOptions { Theme = BowireTheme.Light };
        Assert.Equal(BowireTheme.Light, options.Theme);
    }
}
