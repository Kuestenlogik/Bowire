// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Help;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Minimal-frame HTML wrapper used by the standalone
/// <c>/help/topic/{id}</c> endpoint (#324). The Help rail's "Open in
/// new tab" affordance lands here so a multi-monitor operator can pop
/// the docs out to a separate window while keeping the workbench live.
/// </summary>
/// <remarks>
/// <para>
/// The page is intentionally thin: links the workbench's
/// <c>bowire.css</c> so typography matches the in-app rendering, a
/// "Back to Bowire" anchor across the top, and the server-rendered
/// <see cref="HelpTopic.BodyHtml"/> dropped into a single content
/// column. No drawer chrome, no rail strip, no sidebar — the operator
/// already navigated to a specific topic, so the standalone page just
/// shows that topic.
/// </para>
/// <para>
/// We intentionally do NOT depend on the JS bundle here. The page
/// renders without a single byte of JavaScript so a tab the operator
/// left open survives the workbench process restarting (the rail
/// would have to reload + reinitialise; the standalone tab stays
/// readable).
/// </para>
/// </remarks>
internal static class StandaloneHelpHtml
{
    public static string Topic(string basePath, HelpTopic topic)
    {
        // Prefer the server-rendered HTML body. Older provider builds
        // (or third-party providers) might emit Markdown only; fall
        // back to a <pre> dump so the page still renders something
        // useful instead of a blank panel.
        var body = !string.IsNullOrEmpty(topic.BodyHtml)
            ? topic.BodyHtml
            : "<pre>" + WebUtility.HtmlEncode(topic.Markdown ?? string.Empty) + "</pre>";
        return Frame(basePath, topic.Title, body);
    }

    public static string NotFound(string basePath, string id)
    {
        var safeId = WebUtility.HtmlEncode(id);
        return Frame(basePath,
            title: "Topic not found",
            body: "<h1>Topic not found</h1>"
                + $"<p>No help topic registered with id <code>{safeId}</code>.</p>"
                + $"<p><a href=\"{basePath}/\">Back to Bowire</a></p>");
    }

    public static string NotInstalled(string basePath)
        => Frame(basePath,
            title: "Help not installed",
            body: "<h1>Help not installed</h1>"
                + "<p>This Bowire host doesn't have <code>Kuestenlogik.Bowire.Help</code> "
                + "registered. Install the package and call <code>builder.AddBowireHelp()</code> "
                + "to enable in-app docs.</p>"
                + $"<p><a href=\"{basePath}/\">Back to Bowire</a></p>");

    private static string Frame(string basePath, string title, string body)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        // The base path is server-supplied (not user input) but we run
        // it through encoding anyway so a quirky configured prefix
        // (e.g. one with an embedded quote) can't escape an attribute.
        var safeBase = WebUtility.HtmlEncode(basePath);
        var backHref = string.IsNullOrEmpty(basePath) ? "/" : safeBase + "/";
        // Use `$$"""` so the interpolation delimiter is `{{` / `}}` —
        // single-brace pairs in the CSS body pass through verbatim.
        return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>{{safeTitle}} — Bowire help</title>
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <link rel="stylesheet" href="{{safeBase}}/bowire.css">
  <style>
    /* Standalone-only frame. Workbench CSS supplies typography +
       colour tokens; this stylesheet just lays the page out as a
       single readable column without the rail / sidebar / drawer
       chrome the in-app rendering wraps around it. */
    body {
      margin: 0;
      padding: 24px;
      background: var(--bowire-bg, #fff);
      color: var(--bowire-text, #111);
      font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
    }
    .bowire-help-standalone-frame {
      max-width: 880px;
      margin: 0 auto;
    }
    .bowire-help-standalone-back {
      display: inline-block;
      margin-bottom: 16px;
      padding: 6px 10px;
      background: var(--bowire-surface, #f4f4f4);
      border-radius: 4px;
      color: var(--bowire-text, #111);
      text-decoration: none;
      font-size: 13px;
    }
    .bowire-help-standalone-back:hover {
      background: var(--bowire-surface-hover, #e8e8e8);
    }
  </style>
</head>
<body>
  <div class="bowire-help-standalone-frame">
    <a class="bowire-help-standalone-back" href="{{backHref}}">&larr; Back to Bowire</a>
    <article class="bowire-help-content">{{body}}</article>
  </div>
</body>
</html>
""";
    }
}
