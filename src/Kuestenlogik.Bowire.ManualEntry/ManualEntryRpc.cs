// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Grpc.Core;
using Kuestenlogik.Bowire.ManualEntry.V1;

namespace Kuestenlogik.Bowire.ManualEntry;

/// <summary>
/// The handler behind <c>ListManualEntryForms</c>: the forms of the plugins
/// that describe one, checked, ordered by protocol id and paged.
/// </summary>
internal static partial class ManualEntryRpc
{
    internal const int DefaultPageSize = 50;
    internal const int MaxPageSize = 200;

    /// <summary>One page of the forms of <paramref name="protocols"/>.</summary>
    /// <param name="protocols">The loaded protocol plugins.</param>
    /// <param name="request">Page size and token.</param>
    /// <param name="onRejected">Told about every form that is left out, and why.</param>
    public static ListManualEntryFormsResponse List(
        IEnumerable<IBowireProtocol> protocols,
        ListManualEntryFormsRequest request,
        Action<string, IReadOnlyList<string>>? onRejected = null)
    {
        ArgumentNullException.ThrowIfNull(protocols);
        ArgumentNullException.ThrowIfNull(request);
        if (request.PageSize < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "page_size must not be negative."));
        var size = request.PageSize == 0 ? DefaultPageSize : Math.Min(request.PageSize, MaxPageSize);

        // Ordered by protocol id, so the token (the last id of the previous
        // page) is a position that holds while plugins come and go.
        var described = protocols
            .Where(p => p is IBowireManualEntry)
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .Where(p => string.CompareOrdinal(p.Id, request.PageToken) > 0)
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        var response = new ListManualEntryFormsResponse();
        var index = 0;
        for (; index < described.Count && response.Forms.Count < size; index++)
        {
            var protocol = described[index];
            // A plugin with nothing to describe is not listed, and is not an error.
            if (((IBowireManualEntry)protocol).DescribeManualEntry() is not { } form) continue;
            var problems = Problems(form);
            if (problems.Count > 0)
            {
                onRejected?.Invoke(protocol.Id, problems);
                continue;
            }
            response.Forms.Add(ToMessage(protocol.Id, form));
        }
        if (index < described.Count) response.NextPageToken = described[index - 1].Id;
        return response;
    }

    /// <summary>
    /// What is wrong with a form, or nothing. A form with a problem would
    /// assemble a call the plugin cannot take, so it is not served at all.
    /// </summary>
    public static IReadOnlyList<string> Problems(BowireManualEntryForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var problems = new List<string>();
        if (form.CallKinds.Count == 0) problems.Add("it offers no call kind");

        var kindIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in form.CallKinds)
        {
            if (string.IsNullOrWhiteSpace(kind.Id)) problems.Add("a call kind has no id");
            else if (!kindIds.Add(kind.Id)) problems.Add($"call kind '{kind.Id}' is declared twice");
            if (!BowireManualCallKind.MethodTypes.Contains(kind.MethodType, StringComparer.Ordinal))
                problems.Add($"call kind '{kind.Id}' has method type '{kind.MethodType}'");
        }

        var fieldKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in form.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key)) problems.Add("a field has no key");
            else if (!fieldKeys.Add(field.Key)) problems.Add($"field '{field.Key}' is declared twice");
            if (field.Type == "select" && field.Options.Count == 0)
                problems.Add($"select field '{field.Key}' has no options");
            foreach (var kindId in field.CallKinds)
            {
                if (!kindIds.Contains(kindId)) problems.Add($"field '{field.Key}' names unknown call kind '{kindId}'");
            }
        }

        foreach (var kind in form.CallKinds)
        {
            foreach (var key in Placeholders(kind.Service).Concat(Placeholders(kind.Method)))
            {
                if (!fieldKeys.Contains(key))
                    problems.Add($"call kind '{kind.Id}' uses the placeholder '{key}', which no field declares");
            }
        }
        return problems;
    }

    /// <summary>The <c>{key}</c> placeholders of a template.</summary>
    internal static IEnumerable<string> Placeholders(string? template) =>
        PlaceholderPattern().Matches(template ?? "").Select(m => m.Groups[1].Value);

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex PlaceholderPattern();

    private static ManualEntryForm ToMessage(string protocol, BowireManualEntryForm form)
    {
        var message = new ManualEntryForm { Protocol = protocol, UrlPlaceholder = form.UrlPlaceholder ?? "" };
        foreach (var kind in form.CallKinds)
        {
            message.CallKinds.Add(new CallKind
            {
                Id = kind.Id,
                Label = kind.Label,
                MethodType = kind.MethodType,
                Service = kind.Service,
                Method = kind.Method,
                Description = kind.Description ?? "",
                BodyExample = kind.BodyExample ?? "",
            });
        }
        foreach (var field in form.Fields)
        {
            var f = new Field
            {
                Key = field.Key,
                Label = field.Label,
                Placement = field.Placement == BowireManualFieldPlacement.Template
                    ? Placement.Template : Placement.Metadata,
                Description = field.Description ?? "",
                Placeholder = field.Placeholder ?? "",
                Required = field.Required,
                Type = field.Type,
                DefaultValue = field.DefaultValue ?? "",
            };
            f.Options.AddRange(field.Options.Select(o => new Option { Value = o.Value, Label = o.Label }));
            f.CallKinds.AddRange(field.CallKinds);
            message.Fields.Add(f);
        }
        return message;
    }
}
