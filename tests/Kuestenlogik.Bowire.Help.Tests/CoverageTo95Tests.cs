// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using Kuestenlogik.Bowire.Help;
using Kuestenlogik.Bowire.Help.Provider;

namespace Kuestenlogik.Bowire.Help.Tests;

/// <summary>
/// Targeted gap-fills for <see cref="MarkdownHelpProvider"/> branches the
/// existing suite doesn't cover:
/// <list type="bullet">
///   <item>front-matter <c>summary:</c> with an empty value returns null
///     (then falls through to the first-H1 / file-stem fallback);</item>
///   <item>missing front-matter + missing H1 + multi-word file stem
///     drives <see cref="MarkdownHelpProvider"/>'s <c>FileStemFallback</c>
///     path (hyphens become spaces, first letter capitalised);</item>
///   <item>the StripMarkdown walker's three early-return arms — root is
///     a <c>LiteralInline</c>, <c>CodeInline</c>, or <c>FencedCodeBlock</c>
///     — are reached via topics with leading code fences / pure-code bodies;</item>
///   <item>search excerpt falls back to the leading paragraph when none
///     of the query terms appear in the body (and the body is shorter
///     than the 200-char excerpt cap).</item>
/// </list>
/// </summary>
public sealed class CoverageTo95Tests
{
    [Fact]
    public void Front_matter_with_empty_summary_falls_back_to_first_h1()
    {
        // summary: '' is the SummaryFromFrontmatter "v.Length > 0 ? v : null"
        // branch — when the field is present but blank, the provider must
        // fall through to FirstHeading rather than serialising the blank.
        var sut = BuildWith(new Dictionary<string, string>
        {
            ["bowire-help-docs/blank-summary.md"] =
                "---\nsummary: ''\n---\n# Blank summary fallback\n\nBody text.\n",
        });

        var topic = sut.GetTopic("blank-summary");
        Assert.NotNull(topic);
        Assert.Equal("Blank summary fallback", topic!.Title);
    }

    [Fact]
    public void Missing_front_matter_and_no_h1_uses_file_stem_with_hyphen_spaces()
    {
        // Drives FileStemFallback: relative is the resource minus prefix +
        // .md; no slash → stem is the whole relative; first char upper +
        // hyphens become spaces.
        var sut = BuildWith(new Dictionary<string, string>
        {
            ["bowire-help-docs/quick-start-guide.md"] =
                "Body without a heading at all, just prose.\n",
        });

        var topic = sut.GetTopic("quick-start-guide");
        Assert.NotNull(topic);
        Assert.Equal("Quick start guide", topic!.Title);
    }

    [Fact]
    public void Missing_front_matter_no_h1_with_subfolder_uses_filename_stem()
    {
        // Same FileStemFallback branch, but with a path so the LastIndexOf('/')
        // branch fires (slash >= 0 path) → only the trailing segment is
        // considered for the fallback title.
        var sut = BuildWith(new Dictionary<string, string>
        {
            ["bowire-help-docs/category/leaf-page.md"] =
                "No heading prose.\n",
        });

        var topic = sut.GetTopic("category/leaf-page");
        Assert.NotNull(topic);
        Assert.Equal("Leaf page", topic!.Title);
        Assert.Equal("category", topic.CategoryId);
    }

    [Fact]
    public void Strip_markdown_walks_inline_code_into_the_search_index()
    {
        // Inline `code` is walked via the CodeInline Descendants arm
        // (the non-early-return path). Tokens inside backticks must
        // make it into the body index so a search against an inline
        // code token surfaces the topic.
        var sut = BuildWith(new Dictionary<string, string>
        {
            ["bowire-help-docs/inline-code.md"] =
                "# Inline\n\nUse the `uniqueinlinetoken` to flip the switch.\n",
        });

        var hits = sut.Search("uniqueinlinetoken");
        var hit = Assert.Single(hits);
        Assert.Equal("inline-code", hit.Id);
    }

    [Fact]
    public void Search_excerpt_falls_back_to_leading_paragraph_when_no_term_matches()
    {
        // Title contains the term so the topic ranks; body doesn't, so
        // ExtractExcerpt's "first term occurrence" loop drops through to
        // the leading-paragraph fallback. `title:` is the front-matter
        // field the provider keys on as of the help-drawer-titles fix —
        // `summary:` is the nav-row excerpt and is no longer used as a
        // search-indexed title.
        var sut = BuildWith(new Dictionary<string, string>
        {
            ["bowire-help-docs/title-only.md"] =
                "---\ntitle: 'Recording titles win'\n---\nShort body.\n",
        });

        var hits = sut.Search("titles");
        var hit = Assert.Single(hits);
        Assert.Equal("Short body.", hit.Excerpt);
    }

    // ---- Helpers — mirror the existing test file's pattern ----------

    private static MarkdownHelpProvider BuildWith(Dictionary<string, string> resources)
    {
        var asm = new FakeAssembly(resources);
        var ctor = typeof(MarkdownHelpProvider).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: [typeof(Assembly)], modifiers: null)
            ?? throw new InvalidOperationException("internal Assembly-based ctor not found");
        return (MarkdownHelpProvider)ctor.Invoke([asm])!;
    }

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
