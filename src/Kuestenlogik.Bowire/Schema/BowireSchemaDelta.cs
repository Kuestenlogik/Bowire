// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Kuestenlogik.Bowire.Schema;

/// <summary>
/// A reference to one method inside a service, used in the added/removed
/// buckets of a <see cref="BowireSchemaDelta"/>. <paramref name="Method"/> is
/// the method's full name — the identity the diff aligns on.
/// </summary>
public sealed record BowireMethodRef(string Service, string Method);

/// <summary>
/// A changed method plus which facet moved. <paramref name="Kind"/> is one of:
/// <c>signature</c> (the callable surface moved — route, invocation type, or
/// request/response shape), <c>deprecation</c> (the deprecated flag flipped
/// while the surface stayed intact), or <c>annotation</c> (a prose-only edit).
/// <paramref name="Detail"/> is a short human line naming the facet(s).
/// </summary>
public sealed record BowireMethodChange(string Service, string Method, string Kind, string Detail);

/// <summary>
/// The delta between two API discovery snapshots (base → head): which services
/// and methods were added, removed, or changed. This is the C# counterpart of
/// the workbench's #185 schema-watch delta (<c>wwwroot/js/api.js</c>
/// <c>schemaDiff</c>), so the CLI, the PR bot, and the live workbench classify
/// a schema change the same way. Produced by <see cref="BowireSchemaDiff"/>.
/// </summary>
public sealed record BowireSchemaDelta(
    IReadOnlyList<string> AddedServices,
    IReadOnlyList<string> RemovedServices,
    IReadOnlyList<BowireMethodRef> AddedMethods,
    IReadOnlyList<BowireMethodRef> RemovedMethods,
    IReadOnlyList<BowireMethodChange> ChangedMethods,
    IReadOnlyList<BowireMethodChange> AnnotatedMethods)
{
    /// <summary>An empty delta — the two snapshots are schema-identical.</summary>
    public static BowireSchemaDelta Empty { get; } = new([], [], [], [], [], []);

    /// <summary>
    /// True when anything on the callable surface moved (a service or method
    /// added/removed, or a signature/deprecation change). Prose-only annotation
    /// edits do NOT set this — the same discipline the workbench applies so a
    /// description edit never trips an alert.
    /// </summary>
    public bool CallableMoved =>
        AddedServices.Count + RemovedServices.Count +
        AddedMethods.Count + RemovedMethods.Count + ChangedMethods.Count > 0;

    /// <summary>
    /// True when a consumer could break: a removed service or method, or a
    /// signature change. Additions and pure deprecation/annotation edits are
    /// non-breaking. This is what a PR check's <c>fail-on=breaking</c> gates on.
    /// </summary>
    public bool HasBreakingChanges =>
        RemovedServices.Count > 0 ||
        RemovedMethods.Count > 0 ||
        ChangedMethods.Any(c => string.Equals(c.Kind, "signature", StringComparison.Ordinal));

    /// <summary>True when neither the callable surface nor any annotation moved.</summary>
    public bool IsEmpty => !CallableMoved && AnnotatedMethods.Count == 0;

    /// <summary>
    /// One-line summary, e.g. <c>+1 service, +2 methods, ~1 changed</c>. ASCII
    /// on purpose (mirrors the workbench <c>schemaDeltaSummary</c> but drops the
    /// Unicode +/−/± so the line survives any CI log or bash pipeline untouched).
    /// </summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (AddedServices.Count > 0) parts.Add($"+{AddedServices.Count} service{Plural(AddedServices.Count)}");
        if (RemovedServices.Count > 0) parts.Add($"-{RemovedServices.Count} service{Plural(RemovedServices.Count)}");
        if (AddedMethods.Count > 0) parts.Add($"+{AddedMethods.Count} method{Plural(AddedMethods.Count)}");
        if (RemovedMethods.Count > 0) parts.Add($"-{RemovedMethods.Count} method{Plural(RemovedMethods.Count)}");
        if (ChangedMethods.Count > 0) parts.Add($"~{ChangedMethods.Count} changed");
        if (AnnotatedMethods.Count > 0) parts.Add($"~{AnnotatedMethods.Count} note{Plural(AnnotatedMethods.Count)}");
        return parts.Count > 0 ? string.Join(", ", parts) : "schema identical";
    }

    /// <summary>
    /// Render the delta as the "API schema" section of the PR-bot markdown
    /// comment: a summary line, then a bullet list per bucket. Pure ASCII so it
    /// travels cleanly through the GitHub API and any CI shell.
    /// </summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("**API schema:** ").Append(Summary()).Append(".\n");

        AppendServiceList(sb, "Removed services", RemovedServices);
        AppendServiceList(sb, "Added services", AddedServices);
        AppendMethodList(sb, "Removed methods", RemovedMethods);
        AppendMethodList(sb, "Added methods", AddedMethods);
        AppendChangeList(sb, "Signature changes", ChangedMethods, "signature");
        AppendChangeList(sb, "Deprecation changes", ChangedMethods, "deprecation");

        return sb.ToString();
    }

    private static void AppendServiceList(StringBuilder sb, string heading, IReadOnlyList<string> services)
    {
        if (services.Count == 0) return;
        sb.Append("\n**").Append(heading).Append("**\n");
        foreach (var s in services) sb.Append("- `").Append(s).Append("`\n");
    }

    private static void AppendMethodList(StringBuilder sb, string heading, IReadOnlyList<BowireMethodRef> methods)
    {
        if (methods.Count == 0) return;
        sb.Append("\n**").Append(heading).Append("**\n");
        foreach (var m in methods) sb.Append("- `").Append(m.Service).Append("` `").Append(m.Method).Append("`\n");
    }

    private static void AppendChangeList(StringBuilder sb, string heading, IReadOnlyList<BowireMethodChange> changes, string kind)
    {
        var matching = changes.Where(c => string.Equals(c.Kind, kind, StringComparison.Ordinal)).ToList();
        if (matching.Count == 0) return;
        sb.Append("\n**").Append(heading).Append("**\n");
        foreach (var c in matching)
            sb.Append("- `").Append(c.Service).Append("` `").Append(c.Method).Append("`: ").Append(c.Detail).Append('\n');
    }

    private static string Plural(int n) => n == 1 ? "" : "s";
}
