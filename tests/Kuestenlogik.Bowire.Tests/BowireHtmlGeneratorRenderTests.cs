// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Render-path tests for <see cref="BowireHtmlGenerator"/>. The class is
/// internal so these live next door, with InternalsVisibleTo enabled.
/// We feed in a hand-rolled <see cref="DefaultHttpContext"/> request and
/// assert on the resulting HTML — the JS-string escaping, the route prefix
/// stripping, the merged ServerUrls handling, and the Standalone-mode flag
/// flip.
/// </summary>
public class BowireHtmlGeneratorRenderTests
{
    private static HttpRequest BuildRequest()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("example.com", 443);
        return ctx.Request;
    }

    [Fact]
    public void GenerateIndexHtml_Embeds_Title_Description_And_Theme()
    {
        var options = new BowireOptions
        {
            Title = "Workbench",
            Description = "Staging API",
            Theme = BowireTheme.Light,
            RoutePrefix = "ui",
        };

        var html = BowireHtmlGenerator.GenerateIndexHtml(options, BuildRequest());

        Assert.Contains("<title>Workbench — Staging API</title>", html, StringComparison.Ordinal);
        Assert.Contains("data-theme=\"light\"", html, StringComparison.Ordinal);
        Assert.Contains("title: \"Workbench\"", html, StringComparison.Ordinal);
        Assert.Contains("description: \"Staging API\"", html, StringComparison.Ordinal);
        Assert.Contains("theme: \"light\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateIndexHtml_Strips_Leading_And_Trailing_Slashes_From_Route_Prefix()
    {
        var options = new BowireOptions { RoutePrefix = "/tools/api/" };

        var html = BowireHtmlGenerator.GenerateIndexHtml(options, BuildRequest());

        Assert.Contains("prefix: \"/tools/api\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateIndexHtml_Renders_Internal_Services_Toggle_From_Options()
    {
        var optionsHidden = new BowireOptions { ShowInternalServices = false };
        var optionsVisible = new BowireOptions { ShowInternalServices = true };

        var hiddenHtml = BowireHtmlGenerator.GenerateIndexHtml(optionsHidden, BuildRequest());
        var visibleHtml = BowireHtmlGenerator.GenerateIndexHtml(optionsVisible, BuildRequest());

        Assert.Contains("showInternalServices: false", hiddenHtml, StringComparison.Ordinal);
        Assert.Contains("showInternalServices: true", visibleHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateIndexHtml_Lock_Server_Url_Flag_Round_Trips()
    {
        var options = new BowireOptions { LockServerUrl = true };

        var html = BowireHtmlGenerator.GenerateIndexHtml(options, BuildRequest());

        Assert.Contains("lockServerUrl: true", html, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateIndexHtml_Embedded_Mode_Sets_True_Flag()
    {
        var options = new BowireOptions { Mode = BowireMode.Embedded };

        var html = BowireHtmlGenerator.GenerateIndexHtml(options, BuildRequest());

        Assert.Contains("embeddedMode: true", html, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateIndexHtml_Standalone_Mode_Sets_False_Flag()
    {
        var options = new BowireOptions { Mode = BowireMode.Standalone };

        var html = BowireHtmlGenerator.GenerateIndexHtml(options, BuildRequest());

        Assert.Contains("embeddedMode: false", html, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateIndexHtml_Escapes_Quotes_And_Backslashes_In_Title()
    {
        // EscapeJs replaces the backslash, the double-quote, and the LF
        // before interpolation into the JSON-like inline config block.
        var options = new BowireOptions
        {
            Title = "He said \"hi\"\\\nworld",
            Description = "x",
        };

        var html = BowireHtmlGenerator.GenerateIndexHtml(options, BuildRequest());

        Assert.Contains("title: \"He said \\\"hi\\\"\\\\\\nworld\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateIndexHtml_Merges_Legacy_ServerUrl_Into_ServerUrls_Array()
    {
        // The legacy single ServerUrl gets prepended to ServerUrls when
        // it isn't already in the list. The output array reflects that.
        var options = new BowireOptions
        {
            ServerUrl = "https://api.legacy",
        };
        options.ServerUrls.Add("https://api.new");

        var html = BowireHtmlGenerator.GenerateIndexHtml(options, BuildRequest());

        Assert.Contains("\"https://api.legacy\"", html, StringComparison.Ordinal);
        Assert.Contains("\"https://api.new\"", html, StringComparison.Ordinal);
        var legacyIdx = html.IndexOf("https://api.legacy", StringComparison.Ordinal);
        var newIdx = html.IndexOf("https://api.new", StringComparison.Ordinal);
        Assert.True(legacyIdx > 0 && newIdx > 0);
        Assert.True(legacyIdx < newIdx,
            "Legacy ServerUrl must be inserted before the explicit ServerUrls entries.");
    }

    [Fact]
    public void GenerateIndexHtml_ServerUrl_Already_In_ServerUrls_Is_Not_Duplicated()
    {
        var options = new BowireOptions
        {
            ServerUrl = "https://api.example",
        };
        options.ServerUrls.Add("https://api.example");

        var html = BowireHtmlGenerator.GenerateIndexHtml(options, BuildRequest());

        // The merged JSON array contains the URL exactly once.
        var jsonStart = html.IndexOf("serverUrls:", StringComparison.Ordinal);
        Assert.True(jsonStart > 0);
        var jsonLine = html.Substring(jsonStart, Math.Min(120, html.Length - jsonStart));
        var firstUrl = jsonLine.IndexOf("https://api.example", StringComparison.Ordinal);
        var secondUrl = jsonLine.IndexOf("https://api.example", firstUrl + 1, StringComparison.Ordinal);
        Assert.True(firstUrl > 0);
        Assert.Equal(-1, secondUrl);
    }

    [Fact]
    public void GenerateIndexHtml_Empty_Server_Url_Renders_Empty_String()
    {
        var options = new BowireOptions(); // ServerUrl null

        var html = BowireHtmlGenerator.GenerateIndexHtml(options, BuildRequest());

        Assert.Contains("serverUrl: \"\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateIndexHtml_Carries_Assembly_Version_From_Manifest()
    {
        var options = new BowireOptions();

        var html = BowireHtmlGenerator.GenerateIndexHtml(options, BuildRequest());

        // Either the InformationalVersion or the file version — both are
        // non-empty on a built assembly. We don't pin the exact string
        // because every release rolls it.
        Assert.Contains("version:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("version: \"\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("version: \"unknown\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateIndexHtml_Inlines_Css_And_Js_Embedded_Resources()
    {
        var options = new BowireOptions();

        var html = BowireHtmlGenerator.GenerateIndexHtml(options, BuildRequest());

        // The HTML opens with <!DOCTYPE html> and contains a <style> block
        // pulled from the embedded bowire.css plus a <script> with the
        // concatenated bowire.js. Pin the structural markers; trust the
        // build to populate the contents.
        Assert.Contains("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("<style>", html, StringComparison.Ordinal);
        Assert.Contains("<div id=\"bowire-app\">", html, StringComparison.Ordinal);
        Assert.Contains("window.__BOWIRE_CONFIG__", html, StringComparison.Ordinal);
    }
}
