---
title: Ship a help provider
summary: 'Implement IBowireHelpProvider to back the workbench Help rail + F1 surface + /api/help/* endpoints with your own documentation source.'
---

# Ship a help provider

The **Help rail** + the F1 quick-help affordance + the `/api/help/*` endpoint stack all read from a single SPI: `IBowireHelpProvider`. The core package ships **no** implementation. The bundled `Kuestenlogik.Bowire.Help` package provides one over the embedded `docs/` markdown set; when no implementation is registered the help endpoints return HTTP 501 and the workbench renders Help affordances as disabled (via `/api/help/available`).

Reach for this seam when you want to back the workbench's in-app docs with your own source — a custom CMS, a knowledge base, a different markdown set, a federated provider that merges several sources.

## The interface

`IBowireHelpProvider` lives in `src/Kuestenlogik.Bowire/Help/IBowireHelpProvider.cs`. The public surface and its supporting records:

```csharp
public interface IBowireHelpProvider
{
    HelpTopic? GetTopic(string id);
    IReadOnlyList<HelpSearchHit> Search(string query, int limit = 20);
    IReadOnlyList<HelpTopicSummary> ListTopics();
}

public sealed record HelpTopic(
    string Id,
    string Title,
    string? Summary,
    string Markdown,
    string BodyHtml,
    string? CategoryId);

public sealed record HelpTopicSummary(
    string Id, string Title, string? Summary, string? CategoryId);

public sealed record HelpSearchHit(
    string Id, string Title, string Excerpt, double Score);
```

What each method does:

- **`GetTopic(string id)`** — look up a single topic by its stable id. Return `null` when no topic matches — the `/api/help/topic/{id}` endpoint surfaces that as HTTP 404 (distinct from the "no provider installed" 501).
- **`Search(string query, int limit = 20)`** — free-text search across topic titles + bodies. Implementations own the ranking; the workbench just renders the order returned. Empty / whitespace queries return an empty list. `Score` is opaque to the workbench — only order matters — but stabilising on a 0..1 or 0..100 range is the natural future convention.
- **`ListTopics()`** — enumerate every topic the provider can serve. The Help drawer / rail renders this as a topic tree, grouped by `HelpTopicSummary.CategoryId` (null = top level).

The `HelpTopic` body ships in two shapes: the raw `Markdown` source (kept for back-compat + tools that want the original) and `BodyHtml` — sanitised HTML the provider produced server-side so the workbench can `innerHTML` it without re-parsing markdown in the browser. The drawer used to ship a mini-renderer that choked on embedded HTML (DocFX `<dl>`, `<picture>`, theme-aware SVG); rendering server-side keeps the UI thin and lets DocFX-shaped pages render correctly.

## Minimal working example

The bundled `MarkdownHelpProvider` (`src/Kuestenlogik.Bowire.Help/MarkdownHelpProvider.cs`) reads its corpus from an embedded markdown set and serves the three contract methods. The shape worth quoting:

```csharp
public sealed class MarkdownHelpProvider : IBowireHelpProvider
{
    private const string ResourcePrefix = "bowire-help-docs/";

    private readonly Dictionary<string, HelpTopic> _topics;
    private readonly Dictionary<string, IReadOnlyList<string>> _index;

    public MarkdownHelpProvider() : this(typeof(MarkdownHelpProvider).Assembly) { }

    internal MarkdownHelpProvider(Assembly source)
    {
        // Scan source.GetManifestResourceNames() for "bowire-help-docs/*",
        // parse front-matter + first H1 to extract title + summary,
        // render Markdig → HTML once at startup,
        // build a word → topic-ids inverted index for Search().
    }

    public HelpTopic? GetTopic(string id) =>
        _topics.TryGetValue(id, out var topic) ? topic : null;

    public IReadOnlyList<HelpSearchHit> Search(string query, int limit = 20) { ... }

    public IReadOnlyList<HelpTopicSummary> ListTopics() =>
        _topics.Values
            .OrderBy(t => t.CategoryId, StringComparer.Ordinal)
            .ThenBy(t => t.Title, StringComparer.Ordinal)
            .Select(t => new HelpTopicSummary(t.Id, t.Title, t.Summary, t.CategoryId))
            .ToList();
}
```

The cost (parse + index ~60 markdown files, ~300 KB total) is paid once at construction; the provider is registered as a singleton.

For an alternative-backed provider, the contract requirements are: every `GetTopic` lookup must resolve to a `HelpTopic` with both `Markdown` and `BodyHtml` populated (workbench prefers `BodyHtml`), `Search` should rank meaningfully (titles weigh more than body matches in the bundled impl), and `ListTopics` should be cheap — the workbench can call it on every Help-rail render.

## Registration

The bundled package exposes an explicit DI extension (`src/Kuestenlogik.Bowire.Help/BowireHelpServiceCollectionExtensions.cs`):

```csharp
public static class BowireHelpServiceCollectionExtensions
{
    public static IServiceCollection AddBowireHelp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IBowireHelpProvider, MarkdownHelpProvider>();
        return services;
    }
}
```

For your own provider, the registration shape is the same — register `IBowireHelpProvider` as a singleton against your implementation type before the workbench endpoints run:

```csharp
builder.Services.AddSingleton<IBowireHelpProvider, MyCustomHelpProvider>();
```

The four help endpoints (`/api/help/available`, `/api/help/topics`, `/api/help/topic/{id}`, `/api/help/search`) resolve `IBowireHelpProvider` from DI. `/api/help/available` reports whether a provider is registered so the workbench can grey out the Help affordances when no package ships one; the other three endpoints return HTTP 501 when no provider exists.

The bundled `Kuestenlogik.Bowire.Help` package also ships its own `BowireHelpRailContribution` so referencing the package gets both the provider and the rail icon. If you ship a custom provider as a separate package, you'll likely want to either reference `Kuestenlogik.Bowire.Help` (and replace its `MarkdownHelpProvider` registration with a TryAddSingleton-after-Add ordering trick) or contribute your own rail. The simpler route is the former — Help-rail UX is the same regardless of the backing provider.

## See also

- <xref:Kuestenlogik.Bowire.Help.IBowireHelpProvider> — auto-generated interface reference.
- <xref:Kuestenlogik.Bowire.Help.HelpTopic> / <xref:Kuestenlogik.Bowire.Help.HelpTopicSummary> / <xref:Kuestenlogik.Bowire.Help.HelpSearchHit> — supporting record types.
- [Help rail feature page](../features/help-rail.md) — how the workbench renders the topic tree + body, deep-linking, splitter behaviour.
- [Build a rail](rail.md) — the seam that ships the Help rail icon next to the provider.
