// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Schemas;

/// <summary>
/// Computes the <see cref="BowireSchemaDelta"/> between two API discovery
/// snapshots. A faithful C# port of the workbench's #185 schema-watch diff
/// (<c>wwwroot/js/api.js</c>): a method is keyed by its full name within its
/// service; its callable surface is reduced to route / invocation-kind /
/// request-shape / response-shape strings; and a change is classified by which
/// of those facets moved. Field order is normalised (fields sorted by name)
/// so a mere protoc / swagger-gen reorder is not reported as a change — the
/// identity of a field is its name, never its position.
/// </summary>
public static class BowireSchemaDiff
{
    // A schema may reference itself (a node with children of its own type);
    // an unbounded walk would never return. Same bound the workbench uses.
    private const int MaxShapeDepth = 3;

    /// <summary>
    /// Diff two discovery snapshots. <paramref name="before"/> is the base
    /// (e.g. the target branch), <paramref name="after"/> the head.
    /// </summary>
    public static BowireSchemaDelta Compute(
        IReadOnlyList<BowireServiceInfo> before,
        IReadOnlyList<BowireServiceInfo> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var b = Snapshot(before);
        var a = Snapshot(after);

        var addedServices = new List<string>();
        var removedServices = new List<string>();
        var addedMethods = new List<BowireMethodRef>();
        var removedMethods = new List<BowireMethodRef>();
        var changed = new List<BowireMethodChange>();
        var annotated = new List<BowireMethodChange>();

        foreach (var (service, headMethods) in a)
        {
            if (!b.TryGetValue(service, out var baseMethods))
            {
                // A brand-new service: every method is new, but listing them
                // individually would bury the one fact that matters.
                addedServices.Add(service);
                continue;
            }

            foreach (var (method, headRecord) in headMethods)
            {
                if (!baseMethods.TryGetValue(method, out var baseRecord))
                {
                    addedMethods.Add(new BowireMethodRef(service, method));
                    continue;
                }

                var detail = ChangeDetail(baseRecord, headRecord);
                if (detail.Length > 0)
                {
                    changed.Add(new BowireMethodChange(service, method, "signature", detail));
                }
                else if (headRecord.Deprecated != baseRecord.Deprecated)
                {
                    changed.Add(new BowireMethodChange(
                        service, method, "deprecation",
                        headRecord.Deprecated ? "marked deprecated" : "deprecation removed"));
                }
                else if (!string.Equals(headRecord.Note, baseRecord.Note, StringComparison.Ordinal))
                {
                    annotated.Add(new BowireMethodChange(service, method, "annotation", "description updated"));
                }
            }

            foreach (var method in baseMethods.Keys)
            {
                if (!headMethods.ContainsKey(method))
                    removedMethods.Add(new BowireMethodRef(service, method));
            }
        }

        foreach (var service in b.Keys)
        {
            if (!a.ContainsKey(service))
                removedServices.Add(service);
        }

        return new BowireSchemaDelta(
            addedServices, removedServices, addedMethods, removedMethods, changed, annotated);
    }

    /// <summary>
    /// Reduce a service list to <c>service → (method → record)</c>. A method is
    /// keyed by its full name (falling back to its short name); on a duplicate
    /// key the later method wins, mirroring the JS object-assignment semantics
    /// the workbench diff relies on.
    /// </summary>
    private static Dictionary<string, Dictionary<string, MethodRecord>> Snapshot(
        IReadOnlyList<BowireServiceInfo> services)
    {
        var snapshot = new Dictionary<string, Dictionary<string, MethodRecord>>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            var methods = new Dictionary<string, MethodRecord>(StringComparer.Ordinal);
            foreach (var method in service.Methods ?? [])
            {
                var key = !string.IsNullOrEmpty(method.FullName) ? method.FullName : method.Name;
                methods[key] = MethodRecord.From(method);
            }

            snapshot[service.Name] = methods;
        }

        return snapshot;
    }

    /// <summary>
    /// Which facets of the callable surface moved, as a short human line
    /// (<c>route GET /a -> PUT /a, request shape changed</c>). Empty string when
    /// the callable surface is identical.
    /// </summary>
    private static string ChangeDetail(MethodRecord baseRecord, MethodRecord headRecord)
    {
        var bits = new List<string>(4);
        if (!string.Equals(baseRecord.Route, headRecord.Route, StringComparison.Ordinal))
        {
            bits.Add($"route {Route(baseRecord.Route)} -> {Route(headRecord.Route)}");
        }

        if (!string.Equals(baseRecord.Kind, headRecord.Kind, StringComparison.Ordinal))
            bits.Add("invocation type changed");
        if (!string.Equals(baseRecord.Input, headRecord.Input, StringComparison.Ordinal))
            bits.Add("request shape changed");
        if (!string.Equals(baseRecord.Output, headRecord.Output, StringComparison.Ordinal))
            bits.Add("response shape changed");

        return string.Join(", ", bits);

        static string Route(string route) => route.Length > 0 ? route : "(none)";
    }

    /// <summary>
    /// The subset of a method that changes what a caller must send or can
    /// expect back, reduced to comparable strings. Prose (summary/description)
    /// is captured in <see cref="Note"/> but deliberately kept OUT of the
    /// callable surface, so an edit there is an annotation, never a signature
    /// change.
    /// </summary>
    private readonly record struct MethodRecord(
        string Route, string Kind, string Input, string Output, bool Deprecated, string Note)
    {
        public static MethodRecord From(BowireMethodInfo m)
        {
            var route = $"{m.HttpMethod ?? ""} {m.HttpPath ?? ""}".Trim();
            var kind = m.MethodType
                + (m.ClientStreaming ? "|cs" : "")
                + (m.ServerStreaming ? "|ss" : "");
            return new MethodRecord(
                route,
                kind,
                MessageShape(m.InputType, 0),
                MessageShape(m.OutputType, 0),
                m.Deprecated,
                (m.Summary ?? "") + "\n" + (m.Description ?? ""));
        }
    }

    /// <summary>
    /// A stable one-line shape for a message: <c>Name(field:type[]!@source{...})</c>,
    /// fields sorted by name, nested messages recursed to <see cref="MaxShapeDepth"/>.
    /// Two messages with the same fields in a different order produce the same
    /// string.
    /// </summary>
    private static string MessageShape(BowireMessageInfo? message, int depth)
    {
        if (message is null || depth > MaxShapeDepth) return "";

        var fields = (message.Fields ?? [])
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.Append(message.Name).Append('(');
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            if (i > 0) sb.Append(',');
            sb.Append(f.Name).Append(':').Append(f.Type);
            if (f.IsRepeated) sb.Append("[]");
            if (f.Required) sb.Append('!');
            if (!string.IsNullOrEmpty(f.Source)) sb.Append('@').Append(f.Source);
            if (f.MessageType is not null)
                sb.Append('{').Append(MessageShape(f.MessageType, depth + 1)).Append('}');
        }

        sb.Append(')');
        return sb.ToString();
    }
}
