// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Help;

/// <summary>
/// Contract for in-app documentation. The core ships no implementation;
/// the separate <c>Kuestenlogik.Bowire.Help</c> NuGet package provides one
/// over the embedded <c>docs/</c> markdown set. Embedded ASP.NET hosts
/// that don't need workbench docs simply don't reference that package
/// and pay zero cost — the workbench renders Help affordances as
/// disabled (see <c>/api/help/available</c>).
/// </summary>
/// <remarks>
/// Phase 1 ships the SPI + the four endpoints that surface it
/// (<c>/api/help/available</c>, <c>/api/help/topics</c>,
/// <c>/api/help/topic/{id}</c>, <c>/api/help/search</c>). When no
/// implementation is registered the endpoints return HTTP 501 (Not
/// Implemented) so the UI can distinguish "missing package" from
/// "topic not found" (which is 404). Phase 2 builds the
/// <c>Kuestenlogik.Bowire.Help</c> package; phase 3 wires the Help
/// drawer + F1 binding; phases 4–5 cover distribution + cross-cutting
/// integrations (AI lane, MCP tools).
/// </remarks>
public interface IBowireHelpProvider
{
    /// <summary>
    /// Look up a single topic by its stable id. Returns <c>null</c>
    /// when no topic matches — the endpoint surfaces that as 404.
    /// </summary>
    HelpTopic? GetTopic(string id);

    /// <summary>
    /// Free-text search across topic titles + bodies. Implementations
    /// own the ranking; the workbench just renders the order returned.
    /// </summary>
    /// <param name="query">User input. Empty / whitespace returns an empty list.</param>
    /// <param name="limit">Maximum number of hits to return (default 20).</param>
    IReadOnlyList<HelpSearchHit> Search(string query, int limit = 20);

    /// <summary>
    /// Enumerate every topic the provider can serve. The workbench's
    /// Help drawer renders this as a topic tree (grouped by
    /// <see cref="HelpTopicSummary.CategoryId"/>).
    /// </summary>
    IReadOnlyList<HelpTopicSummary> ListTopics();
}

/// <summary>
/// A single help topic in its full form. Markdown is the on-the-wire
/// shape; the workbench renders it client-side so the package
/// doesn't have to ship an HTML pipeline.
/// </summary>
/// <param name="Id">Stable identifier — used in URLs + cross-references.</param>
/// <param name="Title">Display title shown in the drawer header + topic tree.</param>
/// <param name="Markdown">Raw markdown body. May include relative links to other topics by id.</param>
/// <param name="CategoryId">Optional grouping key for the topic tree. <c>null</c> = top level.</param>
public sealed record HelpTopic(string Id, string Title, string Markdown, string? CategoryId);

/// <summary>
/// Lightweight projection of <see cref="HelpTopic"/> for list views.
/// The body is omitted so a Topics call doesn't pull the whole markdown
/// set into the browser when the user only wants to scan titles.
/// </summary>
public sealed record HelpTopicSummary(string Id, string Title, string? CategoryId);

/// <summary>
/// One result from <see cref="IBowireHelpProvider.Search"/>. Carries
/// an excerpt so the result list can show context without a follow-up
/// topic-fetch per hit. The score is opaque to the workbench — only
/// the order matters — but provider implementations may stabilise on
/// 0..1 or 0..100 ranges as a future convention.
/// </summary>
public sealed record HelpSearchHit(string Id, string Title, string Excerpt, double Score);
