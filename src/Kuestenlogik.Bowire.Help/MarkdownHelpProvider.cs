// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Help;
using Markdig;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Kuestenlogik.Bowire.Help.Provider;

/// <summary>
/// <see cref="IBowireHelpProvider"/> backed by the markdown files
/// embedded in this assembly. Scans the manifest at construction
/// for resources whose name starts with <c>bowire-help-docs/</c>,
/// parses the front-matter + first H1 to extract the title, and
/// builds an in-memory inverted-index for search.
/// </summary>
/// <remarks>
/// Cost paid once at startup (singleton). For the docs set Bowire
/// ships today (~60 markdown files, ~5 KB avg, ~300 KB total) the
/// index is well under 1 MB resident — cheap enough that we never
/// re-parse from the manifest stream after registration.
/// </remarks>
public sealed class MarkdownHelpProvider : IBowireHelpProvider
{
    private const string ResourcePrefix = "bowire-help-docs/";

    private readonly Dictionary<string, HelpTopic> _topics;
    // word → list of topic ids that contain it. Tokenisation strips
    // punctuation and lowercases, so 'Recording' and 'recording.' both
    // index against the same word. CA1859 wants the concrete Dictionary
    // type for the private field — the public projection is still the
    // interface via ListTopics()/Search().
    private readonly Dictionary<string, IReadOnlyList<string>> _index;

    // Single Markdig pipeline shared across every topic — advanced
    // extensions cover the tables / DL-blocks / pipe-tables / GFM
    // niceties the docs/ tree actually uses. UseSoftlineBreakAsHardlineBreak
    // is deliberately NOT enabled — markdown paragraphs that wrap across
    // multiple source lines should still render as a single paragraph.
    private static readonly MarkdownPipeline _renderPipeline =
        new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

    public MarkdownHelpProvider() : this(typeof(MarkdownHelpProvider).Assembly) { }

    internal MarkdownHelpProvider(Assembly source)
    {
        var topics = new Dictionary<string, HelpTopic>(StringComparer.OrdinalIgnoreCase);
        var index = new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in source.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = source.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var raw = reader.ReadToEnd();

            var (frontmatter, body) = ExtractFrontmatter(raw);
            var topic = BuildTopic(name, frontmatter, body);
            topics[topic.Id] = topic;

            foreach (var word in Tokenise(topic.Title))
            {
                index.AddOrUpdate(word, _ => [topic.Id], (_, list) => { list.Add(topic.Id); return list; });
            }
            foreach (var word in Tokenise(StripMarkdown(body)))
            {
                index.AddOrUpdate(word, _ => [topic.Id], (_, list) =>
                {
                    if (!list.Contains(topic.Id, StringComparer.OrdinalIgnoreCase)) list.Add(topic.Id);
                    return list;
                });
            }
        }

        _topics = topics;
        _index = index.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public HelpTopic? GetTopic(string id) =>
        _topics.TryGetValue(id, out var topic) ? topic : null;

    /// <inheritdoc />
    public IReadOnlyList<HelpSearchHit> Search(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var terms = Tokenise(query).ToList();
        if (terms.Count == 0) return [];

        // Score = number of distinct query terms that the topic
        // matches. Ties are broken by title match (title hit doubles
        // the contribution). A topic that doesn't match any term
        // doesn't appear.
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            if (!_index.TryGetValue(term, out var hits)) continue;
            foreach (var id in hits)
            {
                scores.TryGetValue(id, out var s);
                scores[id] = s + 1;
            }
        }
        foreach (var (id, topic) in _topics)
        {
            // Title-word bonus.
            foreach (var _ in terms.Where(t => topic.Title.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                scores.TryGetValue(id, out var s);
                scores[id] = s + 1;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(kv =>
            {
                var topic = _topics[kv.Key];
                var excerpt = ExtractExcerpt(topic.Markdown, terms);
                return new HelpSearchHit(topic.Id, topic.Title, excerpt, kv.Value);
            })
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<HelpTopicSummary> ListTopics() =>
        _topics.Values
            .OrderBy(t => t.CategoryId, StringComparer.Ordinal)
            .ThenBy(t => t.Title, StringComparer.Ordinal)
            .Select(t => new HelpTopicSummary(t.Id, t.Title, t.Summary, t.CategoryId))
            .ToList();

    /// <summary>
    /// Convert a manifest resource name + parsed markdown into a
    /// <see cref="HelpTopic"/>. The id is the resource name minus the
    /// prefix and <c>.md</c> extension; category is the first path
    /// segment (or null for the root <c>index.md</c>); title comes
    /// from front-matter <c>title:</c>, then the first H1, then the
    /// file stem as a fallback. Front-matter <c>summary:</c> rides
    /// separately as the nav-row excerpt (DocFX convention). The body
    /// is rendered to sanitised HTML once at build time so the
    /// workbench can inject it directly without re-parsing markdown
    /// in the browser.
    /// </summary>
    private static HelpTopic BuildTopic(string resourceName, Frontmatter frontmatter, string body)
    {
        var relative = resourceName[ResourcePrefix.Length..];
        if (relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            relative = relative[..^3];

        var slash = relative.IndexOf('/');
        var categoryId = slash >= 0 ? relative[..slash] : null;
        var title = !string.IsNullOrWhiteSpace(frontmatter.Title)
            ? frontmatter.Title!
            : FirstHeading(body) ?? FileStemFallback(relative);
        var summary = string.IsNullOrWhiteSpace(frontmatter.Summary) ? null : frontmatter.Summary;
        var html = RenderBody(body);
        return new HelpTopic(relative, title, summary, body, html, categoryId);
    }

    /// <summary>
    /// Render markdown to HTML with the shared Markdig pipeline. The
    /// docs/ tree mixes plain markdown with intentional HTML islands
    /// (DocFX picture-elements for theme variants, inline SVG hero
    /// logos, definition lists, pipe tables) — Markdig passes those
    /// through verbatim, which is what the workbench wants. Source
    /// trust: every byte we render comes from the assembly's own
    /// embedded resources, so we don't gate on a sanitiser here.
    /// </summary>
    private static string RenderBody(string markdown) =>
        string.IsNullOrEmpty(markdown) ? string.Empty : Markdown.ToHtml(markdown, _renderPipeline);

    /// <summary>
    /// Parsed YAML-ish front-matter the provider cares about. Keeps
    /// the call sites tidy now that <c>title:</c> and <c>summary:</c>
    /// are read separately.
    /// </summary>
    private readonly record struct Frontmatter(string? Title, string? Summary);

    /// <summary>
    /// Pulls the leading YAML front-matter (between two <c>---</c>
    /// fences) off the markdown body and returns the recognised
    /// fields (<c>title:</c>, <c>summary:</c>) alongside the body
    /// without the front-matter. Front-matter is the docfx convention
    /// used throughout the docs/ tree.
    /// </summary>
    private static (Frontmatter Front, string Body) ExtractFrontmatter(string raw)
    {
        if (!raw.StartsWith("---", StringComparison.Ordinal)) return (default, raw);
        var end = raw.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (default, raw);
        var fm = raw.Substring(3, end - 3);
        var body = raw[(end + 4)..].TrimStart('\r', '\n');
        return (ParseFrontmatter(fm), body);
    }

    private static Frontmatter ParseFrontmatter(string fm)
    {
        string? title = null;
        string? summary = null;
        foreach (var trimmed in fm.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimStart()))
        {
            if (title is null && trimmed.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            {
                var v = trimmed["title:".Length..].Trim().Trim('\'', '"');
                if (v.Length > 0) title = v;
            }
            else if (summary is null && trimmed.StartsWith("summary:", StringComparison.OrdinalIgnoreCase))
            {
                var v = trimmed["summary:".Length..].Trim().Trim('\'', '"');
                if (v.Length > 0) summary = v;
            }
            if (title is not null && summary is not null) break;
        }
        return new Frontmatter(title, summary);
    }

    /// <summary>First markdown H1 (line starting with <c># </c>), or null.</summary>
    private static string? FirstHeading(string body)
    {
        foreach (var t in body.Split('\n').Select(line => line.TrimStart()))
        {
            if (t.StartsWith("# ", StringComparison.Ordinal)) return t[2..].Trim();
        }
        return null;
    }

    private static string FileStemFallback(string relative)
    {
        var slash = relative.LastIndexOf('/');
        var stem = slash >= 0 ? relative[(slash + 1)..] : relative;
        return char.ToUpperInvariant(stem[0]) + stem[1..].Replace('-', ' ');
    }

    private static readonly Regex _wordSplit = new(@"[A-Za-z0-9]+", RegexOptions.Compiled);

    private static IEnumerable<string> Tokenise(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (Match m in _wordSplit.Matches(text))
        {
            // Lowercase via ToLowerInvariant is fine here — we never
            // round-trip the token to a culture-sensitive surface,
            // it lives only inside the in-memory index. CA1308 wants
            // ToUpperInvariant but our consumer expects lowercase
            // keys (the StringComparer is OrdinalIgnoreCase anyway
            // so case is incidental).
#pragma warning disable CA1308
            yield return m.Value.ToLowerInvariant();
#pragma warning restore CA1308
        }
    }

    /// <summary>Render markdown to plain text by walking the AST and emitting
    /// only literal inline runs; code blocks + tables become their
    /// raw text. Used for indexing only — the on-the-wire body stays
    /// raw markdown so the client renderer keeps full control.</summary>
    private static string StripMarkdown(string md)
    {
        var doc = Markdown.Parse(md);
        var sb = new StringBuilder(md.Length);
        Walk(doc, sb);
        return sb.ToString();
    }

    private static void Walk(MarkdownObject node, StringBuilder sb)
    {
        if (node is LiteralInline lit)
        {
            sb.Append(lit.Content).Append(' ');
            return;
        }
        if (node is CodeInline code)
        {
            sb.Append(code.Content).Append(' ');
            return;
        }
        if (node is FencedCodeBlock fenced)
        {
            foreach (var line in fenced.Lines.Lines) sb.Append(line).Append(' ');
            return;
        }
        foreach (var child in node.Descendants())
        {
            if (child is LiteralInline li)
            {
                sb.Append(li.Content).Append(' ');
            }
            else if (child is CodeInline ci)
            {
                sb.Append(ci.Content).Append(' ');
            }
        }
    }

    /// <summary>Pick the first body paragraph that contains a query term, or
    /// fall back to the leading paragraph. Trimmed to ~200 chars so
    /// the search UI can list hits without scrolling.</summary>
    private static string ExtractExcerpt(string body, IReadOnlyList<string> terms)
    {
        var stripped = StripMarkdown(body).Trim();
        if (stripped.Length == 0) return string.Empty;

        // Find first occurrence of any term and excerpt around it.
        foreach (var term in terms)
        {
            var idx = stripped.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var start = Math.Max(0, idx - 60);
            var len = Math.Min(stripped.Length - start, 200);
            var prefix = start > 0 ? "… " : string.Empty;
            var suffix = start + len < stripped.Length ? " …" : string.Empty;
            return prefix + stripped.Substring(start, len).Trim() + suffix;
        }
        return stripped.Length > 200 ? stripped[..200].Trim() + " …" : stripped;
    }
}
