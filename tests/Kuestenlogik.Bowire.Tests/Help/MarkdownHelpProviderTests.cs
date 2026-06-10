// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Help;
using Kuestenlogik.Bowire.Help.Provider;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Tests.Help;

/// <summary>
/// #154 Phase 2 — verifies the markdown-backed help provider correctly
/// surfaces the embedded docs subset. The full embedded set is the
/// real test fixture: setup/, ui-guide/, features/, protocols/, plus
/// the docs index. We assert structural invariants (topics enumerate,
/// titles + categories non-empty, search ranks plausibly) rather than
/// pinning on specific filenames so the assertions survive content
/// edits.
/// </summary>
public sealed class MarkdownHelpProviderTests
{
    private static readonly MarkdownHelpProvider _provider = new();

    [Fact]
    public void ListTopics_ReturnsTheEmbeddedDocsSet()
    {
        var topics = _provider.ListTopics();

        // Lower bound chosen well below the actual count (~60) so a
        // few file removals don't flake the test, but high enough to
        // catch a full-resource-scan regression.
        Assert.True(topics.Count >= 30, $"Expected at least 30 topics, got {topics.Count}");
    }

    [Fact]
    public void ListTopics_PopulatesCategoryFromFirstPathSegment()
    {
        var topics = _provider.ListTopics();
        var categories = topics
            .Select(t => t.CategoryId)
            .Where(c => c is not null)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        // The four whitelisted subtrees should each show up as a
        // category. The root index.md has CategoryId = null and is
        // counted separately.
        Assert.Contains("setup", categories);
        Assert.Contains("ui-guide", categories);
        Assert.Contains("features", categories);
        Assert.Contains("protocols", categories);
    }

    [Fact]
    public void ListTopics_EveryTitleNonEmpty()
    {
        var topics = _provider.ListTopics();

        foreach (var summary in topics)
        {
            Assert.False(string.IsNullOrWhiteSpace(summary.Title),
                $"Topic '{summary.Id}' has an empty title — front-matter summary / first H1 / file-stem fallback all came up empty.");
        }
    }

    [Fact]
    public void GetTopic_ResolvesByPathlikeId()
    {
        // The index page lives at the root.
        var index = _provider.GetTopic("index");
        Assert.NotNull(index);
        Assert.Null(index!.CategoryId);

        // Pick any setup topic and verify the id shape (category/stem).
        var setupTopic = _provider.ListTopics().First(t => t.CategoryId == "setup");
        var fetched = _provider.GetTopic(setupTopic.Id);
        Assert.NotNull(fetched);
        Assert.StartsWith("setup/", fetched!.Id, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(fetched.Markdown));
    }

    [Fact]
    public void GetTopic_ReturnsNullForUnknownId()
    {
        Assert.Null(_provider.GetTopic("no-such-topic"));
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        Assert.Empty(_provider.Search(string.Empty));
        Assert.Empty(_provider.Search("   "));
    }

    [Fact]
    public void Search_FindsTopicsContainingTheTerm()
    {
        var hits = _provider.Search("recording");
        Assert.NotEmpty(hits);
        // Every hit's id or title or excerpt should plausibly contain
        // the term (case-insensitive). We can't pin on a specific
        // file id without baking content assumptions, but the term
        // should surface somewhere.
        foreach (var hit in hits)
        {
            var combined = hit.Id + " " + hit.Title + " " + hit.Excerpt;
            Assert.Contains("recording", combined, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Search_TitleHitsRankHigherThanBodyHits()
    {
        // Use a term that's in some titles (any of the protocol pages
        // has its protocol name as the title) — search ranks should
        // surface the title-matched topic at the top.
        var hits = _provider.Search("graphql", limit: 5);
        Assert.NotEmpty(hits);
        // The top hit's title should contain the term.
        Assert.Contains("graphql", hits[0].Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Search_RespectsLimit()
    {
        var hits = _provider.Search("the", limit: 3);
        Assert.True(hits.Count <= 3);
    }

    [Fact]
    public void Search_ExcerptContainsQueryTerm()
    {
        var hits = _provider.Search("recording", limit: 5);
        Assert.NotEmpty(hits);
        // At least one hit's excerpt should snip around the term.
        Assert.Contains(hits, h => h.Excerpt.Contains("recording", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddBowireHelp_RegistersTheProviderAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddBowireHelp();
        using var sp = services.BuildServiceProvider();

        var first = sp.GetService<IBowireHelpProvider>();
        var second = sp.GetService<IBowireHelpProvider>();

        Assert.NotNull(first);
        Assert.IsType<MarkdownHelpProvider>(first);
        Assert.Same(first, second);
    }
}
