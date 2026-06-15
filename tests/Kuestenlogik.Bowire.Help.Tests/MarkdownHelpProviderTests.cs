// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using Kuestenlogik.Bowire.Help;
using Kuestenlogik.Bowire.Help.Provider;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Help.Tests;

/// <summary>
/// Behaviour tests for <see cref="MarkdownHelpProvider"/>. Drives
/// the internal Assembly-based ctor with a synthetic
/// <see cref="FakeAssembly"/> that returns hand-rolled markdown
/// resources — gives the index + title + category derivation logic
/// a deterministic input set without depending on the shipped
/// docs/ tree.
/// </summary>
public sealed class MarkdownHelpProviderTests
{
    [Fact]
    public void DefaultCtor_LoadsEmbeddedDocsSet_NonEmpty()
    {
        // Smoke test against the real embedded docs/ — the package
        // bundles ~60 markdown files via EmbeddedResource globs,
        // so ListTopics has to return at least one entry. Also
        // pins the public surface (no ctor args required).
        var sut = new MarkdownHelpProvider();
        var topics = sut.ListTopics();
        Assert.NotEmpty(topics);
        // Every topic carries a non-empty Id + Title.
        Assert.All(topics, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Id));
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
        });
    }

    [Fact]
    public void DefaultCtor_GetTopic_KnownId_ReturnsTopic()
    {
        // 'index' is the root index.md the csproj <EmbeddedResource>
        // glob promotes to bowire-help-docs/index.md.
        var sut = new MarkdownHelpProvider();
        var topic = sut.GetTopic("index");
        Assert.NotNull(topic);
        Assert.Equal("index", topic!.Id);
        Assert.Null(topic.CategoryId);
    }

    [Fact]
    public void GetTopic_UnknownId_ReturnsNull()
    {
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/foo.md"] = "# Foo\n\nbody",
        });

        Assert.Null(sut.GetTopic("does-not-exist"));
    }

    [Fact]
    public void GetTopic_IdLookup_IsCaseInsensitive()
    {
        // The internal Dictionary uses OrdinalIgnoreCase — a typo in
        // the URL the workbench builds (FOO vs foo) shouldn't 404 on
        // an existing topic.
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/setup/standalone.md"] = "# Standalone\n\nbody",
        });

        Assert.NotNull(sut.GetTopic("setup/standalone"));
        Assert.NotNull(sut.GetTopic("SETUP/STANDALONE"));
    }

    [Fact]
    public void BuildTopic_RootIndex_HasNullCategory()
    {
        // index.md sits at the root → no first-segment category.
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/index.md"] = "# Welcome\n\nintro",
        });

        var t = sut.GetTopic("index");
        Assert.NotNull(t);
        Assert.Null(t!.CategoryId);
        Assert.Equal("Welcome", t.Title);
    }

    [Fact]
    public void BuildTopic_NestedPath_FirstSegmentBecomesCategory()
    {
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/setup/docker.md"] = "# Docker setup\n\nbody",
        });

        var t = sut.GetTopic("setup/docker");
        Assert.NotNull(t);
        Assert.Equal("setup", t!.CategoryId);
        Assert.Equal("Docker setup", t.Title);
    }

    [Fact]
    public void BuildTopic_FrontmatterSummary_WinsOverH1()
    {
        // Front-matter `summary:` is the strongest title source — used
        // when authors want the topic-tree label to differ from the
        // first heading.
        var content = "---\nsummary: Friendly title\nother: foo\n---\n# Heading body\n\nbody";
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/x.md"] = content,
        });

        var t = sut.GetTopic("x");
        Assert.NotNull(t);
        Assert.Equal("Friendly title", t!.Title);
    }

    [Fact]
    public void BuildTopic_QuotedFrontmatterSummary_IsUnquoted()
    {
        // Authors sometimes wrap the summary in single/double quotes —
        // the helper strips them so the displayed title doesn't
        // include the punctuation.
        var doubleQuoted = "---\nsummary: \"Quoted title\"\n---\n# H\n";
        var singleQuoted = "---\nsummary: 'Quoted title'\n---\n# H\n";
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/d.md"] = doubleQuoted,
            ["bowire-help-docs/s.md"] = singleQuoted,
        });

        Assert.Equal("Quoted title", sut.GetTopic("d")!.Title);
        Assert.Equal("Quoted title", sut.GetTopic("s")!.Title);
    }

    [Fact]
    public void BuildTopic_NoSummary_FallsBackToFirstH1()
    {
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/x.md"] = "# First Heading\n## Second\n\nbody",
        });

        Assert.Equal("First Heading", sut.GetTopic("x")!.Title);
    }

    [Fact]
    public void BuildTopic_NoSummaryNoH1_FallsBackToFileStem()
    {
        // The stem fallback Title-cases the file name and replaces
        // dashes with spaces — covers docs files that never quite
        // got a heading wired up.
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/quick-start.md"] = "body without heading",
        });

        Assert.Equal("Quick start", sut.GetTopic("quick-start")!.Title);
    }

    [Fact]
    public void BuildTopic_FrontmatterWithoutSummary_FallsBackToH1()
    {
        // Front-matter exists but doesn't carry a summary key —
        // SummaryFromFrontmatter returns null, the body H1 wins.
        var content = "---\nauthor: someone\n---\n# Real Title\n";
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/x.md"] = content,
        });

        Assert.Equal("Real Title", sut.GetTopic("x")!.Title);
    }

    [Fact]
    public void BuildTopic_EmptySummary_FallsBackToH1()
    {
        // summary: with no value (or just whitespace) shouldn't trump
        // the body H1.
        var content = "---\nsummary:\n---\n# Heading\n";
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/x.md"] = content,
        });

        Assert.Equal("Heading", sut.GetTopic("x")!.Title);
    }

    [Fact]
    public void BuildTopic_UnclosedFrontmatter_BodyKeptAsIs()
    {
        // No closing '---' fence — ExtractFrontmatter must treat the
        // whole raw text as body so the user still sees their topic.
        // No H1 in the body either, so the FileStemFallback wins.
        var content = "---\nsummary: stray\nno close fence and no heading";
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/x.md"] = content,
        });

        var t = sut.GetTopic("x");
        Assert.NotNull(t);
        // Title comes from FileStemFallback because no H1 line starts
        // with '# ' in the raw content (the closer is missing so the
        // whole string is the body).
        Assert.Equal("X", t!.Title);
        Assert.Contains("stray", t.Markdown, StringComparison.Ordinal);
    }

    // ---- Search ------------------------------------------------------

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/x.md"] = "# Anything\nlots of text",
        });

        Assert.Empty(sut.Search(""));
        Assert.Empty(sut.Search("   "));
    }

    [Fact]
    public void Search_WhitespaceOnlyTokens_ReturnsEmpty()
    {
        // Tokeniser strips all punctuation — a punctuation-only query
        // yields zero terms and the helper bails before scoring.
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/x.md"] = "# Title\nbody",
        });

        Assert.Empty(sut.Search("!!!"));
    }

    [Fact]
    public void Search_BodyMatch_ReturnsHit_WithExcerpt()
    {
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/a.md"] = "# A topic\nThe recording subsystem watches for changes.",
        });

        var hits = sut.Search("recording");
        var hit = Assert.Single(hits);
        Assert.Equal("a", hit.Id);
        Assert.Contains("recording", hit.Excerpt, StringComparison.OrdinalIgnoreCase);
        Assert.True(hit.Score >= 1);
    }

    [Fact]
    public void Search_TitleMatch_OutranksBodyOnlyMatch()
    {
        // Title contributes both an index hit AND a title-word bonus,
        // so the doc with the term in the title sorts above the
        // body-only match.
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/title-hit.md"] = "# Recording guide\nshort body",
            ["bowire-help-docs/body-hit.md"]  = "# Other\nMentioning recording once down here.",
        });

        var hits = sut.Search("recording");
        Assert.Equal(2, hits.Count);
        Assert.Equal("title-hit", hits[0].Id);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public void Search_MultipleTerms_ScoreIsSumOfDistinctMatches()
    {
        // Doc 'mix' contains both terms, doc 'one' contains one —
        // the mix has the higher score.
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/mix.md"] = "# Setup recording\nbody",
            ["bowire-help-docs/one.md"] = "# Recording basics\nbody",
        });

        var hits = sut.Search("setup recording");
        Assert.Equal(2, hits.Count);
        Assert.Equal("mix", hits[0].Id);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public void Search_LimitClampsResults()
    {
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/a.md"] = "# Topic\nshared word",
            ["bowire-help-docs/b.md"] = "# Topic\nshared word",
            ["bowire-help-docs/c.md"] = "# Topic\nshared word",
        });

        var hits = sut.Search("shared", limit: 2);
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void Search_UnmatchedTerm_ReturnsEmpty()
    {
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/x.md"] = "# Topic\nbody",
        });

        Assert.Empty(sut.Search("nothing-matches"));
    }

    [Fact]
    public void Search_Excerpt_TruncatesLongPlainPrefix()
    {
        // No matching term inside the body — the excerpt falls back
        // to the leading 200 chars plus an ellipsis.
        var body = new string('x', 400);
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/x.md"] = "# Word\n" + body,
        });

        var hits = sut.Search("word");
        var hit = Assert.Single(hits);
        // "Word" is in the title, not the body, so the body-extract
        // path takes the no-term-match branch.
        Assert.EndsWith("…", hit.Excerpt, StringComparison.Ordinal);
        // 200 chars trimmed + " …" suffix.
        Assert.True(hit.Excerpt.Length <= 250);
    }

    // ---- ListTopics --------------------------------------------------

    [Fact]
    public void ListTopics_OrdersByCategoryThenTitle()
    {
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/setup/b.md"] = "# Bravo\n",
            ["bowire-help-docs/setup/a.md"] = "# Alpha\n",
            ["bowire-help-docs/features/x.md"] = "# Xeno\n",
            ["bowire-help-docs/index.md"]   = "# Root\n",
        });

        var topics = sut.ListTopics();
        Assert.Equal(4, topics.Count);
        // null category sorts first under Ordinal (null becomes "" in
        // ordering — verify the leading row is the root index).
        // Then 'features' < 'setup'.
        Assert.Equal("index", topics[0].Id);
        Assert.Equal("features/x", topics[1].Id);
        Assert.Equal("setup/a", topics[2].Id);
        Assert.Equal("setup/b", topics[3].Id);
    }

    [Fact]
    public void ListTopics_OmitsBodyContent()
    {
        // HelpTopicSummary intentionally drops the markdown body so
        // the workbench can load the list view without pulling every
        // topic's full text. Verify there's nothing body-shaped on
        // the projection.
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/x.md"] = "# Title\nlong body content",
        });

        var summary = Assert.Single(sut.ListTopics());
        var summaryType = summary.GetType();
        Assert.Null(summaryType.GetProperty("Markdown"));
        Assert.Null(summaryType.GetProperty("Body"));
    }

    // ---- Manifest filtering ------------------------------------------

    [Fact]
    public void Ctor_IgnoresResourcesOutsideHelpDocsPrefix()
    {
        // Only 'bowire-help-docs/' resources are picked up — anything
        // else in the assembly's manifest is silently skipped.
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/wanted.md"] = "# Wanted\n",
            ["unrelated/skip.md"]           = "# Skip me\n",
            ["bowire-other/skip2.md"]       = "# Skip me too\n",
        });

        var topics = sut.ListTopics();
        var t = Assert.Single(topics);
        Assert.Equal("wanted", t.Id);
    }

    [Fact]
    public void Ctor_IgnoresNonMarkdownExtensionsButKeepsId()
    {
        // The provider strips '.md' but accepts any extension — it's
        // up to the csproj globs to decide what to embed. Non-.md
        // resources keep their full id (extension included).
        var sut = BuildWith(new()
        {
            ["bowire-help-docs/note.txt"] = "# Title\nplain text",
        });

        var t = Assert.Single(sut.ListTopics());
        Assert.Equal("note.txt", t.Id);
    }

    // ---- Helpers -----------------------------------------------------

    private static MarkdownHelpProvider BuildWith(Dictionary<string, string> resources)
    {
        var asm = new FakeAssembly(resources);
        var ctor = typeof(MarkdownHelpProvider).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: [typeof(Assembly)], modifiers: null)
            ?? throw new InvalidOperationException("internal Assembly-based ctor not found");
        return (MarkdownHelpProvider)ctor.Invoke([asm])!;
    }

    /// <summary>
    /// Minimal in-memory <see cref="Assembly"/> that returns the
    /// hand-rolled resource set. Overrides the two methods the
    /// provider uses — everything else stays at the default
    /// (and is never touched on the hot path).
    /// </summary>
    private sealed class FakeAssembly : Assembly
    {
        private readonly Dictionary<string, byte[]> _resources;

        public FakeAssembly(Dictionary<string, string> resources)
        {
            _resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (name, body) in resources)
                _resources[name] = Encoding.UTF8.GetBytes(body);
        }

        public override string[] GetManifestResourceNames() => [.. _resources.Keys];

        public override Stream? GetManifestResourceStream(string name) =>
            _resources.TryGetValue(name, out var bytes)
                ? new MemoryStream(bytes, writable: false)
                : null;
    }
}

/// <summary>
/// Behaviour tests for <see cref="BowireHelpServiceCollectionExtensions"/> —
/// the AddBowireHelp DI registration. Pins the singleton lifetime and the
/// concrete <see cref="MarkdownHelpProvider"/> as the resolved instance.
/// </summary>
public sealed class BowireHelpServiceCollectionExtensionsTests
{
    [Fact]
    public void AddBowireHelp_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => BowireHelpServiceCollectionExtensions.AddBowireHelp(null!));
    }

    [Fact]
    public void AddBowireHelp_ReturnsSameInstance_ForChaining()
    {
        // Service-collection extensions are expected to return the
        // collection for fluent chaining — pin this so a future
        // refactor doesn't quietly break it.
        var sc = new ServiceCollection();
        var returned = sc.AddBowireHelp();
        Assert.Same(sc, returned);
    }

    [Fact]
    public void AddBowireHelp_RegistersIBowireHelpProvider_AsSingleton()
    {
        var sc = new ServiceCollection();
        sc.AddBowireHelp();

        var descriptor = sc.Single(d => d.ServiceType == typeof(IBowireHelpProvider));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(MarkdownHelpProvider), descriptor.ImplementationType);
    }

    [Fact]
    public void AddBowireHelp_ResolvedProvider_IsMarkdownHelpProvider_AndSingleton()
    {
        var sc = new ServiceCollection();
        sc.AddBowireHelp();
        using var sp = sc.BuildServiceProvider();

        var a = sp.GetRequiredService<IBowireHelpProvider>();
        var b = sp.GetRequiredService<IBowireHelpProvider>();
        Assert.Same(a, b);
        Assert.IsType<MarkdownHelpProvider>(a);
    }
}
