// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Help;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The standalone help page served at <c>/help/topic/{id}</c> (#324).
/// </summary>
/// <remarks>
/// This is a page assembled from strings and handed to a browser, so the
/// interesting question is not whether it renders — it is which of those
/// strings reach the document as markup and which do not. A topic id comes
/// off the URL, and the topic body comes from whatever help provider the host
/// registered, which for a third-party provider is not our code.
/// </remarks>
public sealed class StandaloneHelpHtmlTests
{
    private static HelpTopic Topic(string bodyHtml = "<p>Body</p>", string markdown = "# Body", string title = "Recording")
        => new(Id: "recording", Title: title, Summary: null, Markdown: markdown, BodyHtml: bodyHtml, CategoryId: null);

    [Fact]
    public void Topic_Renders_The_Servers_Html_Body_Verbatim()
    {
        // The provider's HTML is already sanitised on the way in; encoding it
        // again here would show readers `&lt;p&gt;` instead of a paragraph.
        var html = StandaloneHelpHtml.Topic("/bowire", Topic(bodyHtml: "<p>Hello <em>there</em></p>"));

        Assert.Contains("<p>Hello <em>there</em></p>", html, StringComparison.Ordinal);
        Assert.Contains("<!doctype html>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Topic_Falls_Back_To_Escaped_Markdown_When_There_Is_No_Html()
    {
        // An older or third-party provider may only produce Markdown. Dumping
        // it in a <pre> keeps the page useful; escaping it is what keeps that
        // fallback from being a way to inject markup the sanitiser never saw.
        var html = StandaloneHelpHtml.Topic("/bowire",
            Topic(bodyHtml: "", markdown: "# Title <script>alert(1)</script>"));

        Assert.Contains("<pre>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Topic_Escapes_The_Title_Into_The_Head()
    {
        var html = StandaloneHelpHtml.Topic("/bowire", Topic(title: "Recording </title><script>x</script>"));

        Assert.DoesNotContain("<script>x</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;/title&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void NotFound_Escapes_The_Id_It_Echoes_Back()
    {
        // The id arrives from the URL, and this page exists precisely for ids
        // that matched nothing — so it is the one place an arbitrary string is
        // guaranteed to be reflected.
        var html = StandaloneHelpHtml.NotFound("/bowire", "<img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Topic not found", html, StringComparison.Ordinal);
    }

    [Fact]
    public void NotInstalled_Names_The_Package_And_The_Call_That_Fixes_It()
    {
        // The page a host lands on when the Help package is absent. A bare
        // "not available" would leave the reader nowhere; the fix is two
        // identifiers and both belong on the page.
        var html = StandaloneHelpHtml.NotInstalled("/bowire");

        Assert.Contains("Kuestenlogik.Bowire.Help", html, StringComparison.Ordinal);
        Assert.Contains("AddBowireHelp()", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/bowire", "/bowire/")]
    [InlineData("", "/")]
    public void The_Back_Link_Points_At_The_Workbench_Root(string basePath, string expectedHref)
    {
        // Embedded hosts mount under a prefix, the standalone tool at the
        // root. An empty base path must not produce href="" — that reloads
        // the help page rather than leaving it.
        var html = StandaloneHelpHtml.Topic(basePath, Topic());

        Assert.Contains($"href=\"{expectedHref}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Quirky_Base_Path_Cannot_Break_Out_Of_An_Attribute()
    {
        // Server-supplied rather than user input, so this is defence in depth
        // — but the whole page hangs off two attributes built from it.
        var html = StandaloneHelpHtml.Topic("/x\" onload=\"alert(1)", Topic());

        Assert.DoesNotContain("onload=\"alert(1)\"", html, StringComparison.Ordinal);
        Assert.Contains("&quot;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Page_Links_The_Workbench_Stylesheet_And_Carries_No_Script()
    {
        // Deliberate: the page renders with no JavaScript so a tab left open
        // survives the workbench process restarting.
        var html = StandaloneHelpHtml.Topic("/bowire", Topic());

        Assert.Contains("/bowire/bowire.css", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }
}
