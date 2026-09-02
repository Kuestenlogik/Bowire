// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuestenlogik.Bowire.Environments;

/// <summary>
/// Merges host-declared environments into what the workbench reads, and keeps
/// them out of what it writes (#49).
/// </summary>
/// <remarks>
/// <para>
/// The workbench sends the whole envelope back on every change. Without the
/// strip on the way in, a host-declared environment would be saved into
/// <c>environments.json</c> on the first edit anybody made — and from then on
/// it would exist twice: once declared and once stored, diverging the moment
/// the host's configuration changed. The merge is the visible half of this
/// feature; the strip is the half that keeps it correct.
/// </para>
/// </remarks>
public static class BowireProvisionedEnvironments
{
    /// <summary>
    /// Add <paramref name="provisioned"/> to an envelope loaded from disk.
    /// </summary>
    /// <param name="envelopeJson">
    /// The stored envelope: <c>globals</c>, <c>environments</c>,
    /// <c>activeEnvId</c>.
    /// </param>
    /// <param name="provisioned">What the host declared. Empty is the common case.</param>
    /// <returns>
    /// The envelope with the declared environments appended, each marked
    /// <c>provisioned: true</c> so the workbench can render them as the host's
    /// rather than as something to edit. Returns the input unchanged when
    /// there is nothing to add or the envelope cannot be parsed — a corrupt
    /// file is the store's problem to report, not this function's to hide.
    /// </returns>
    public static string Merge(string envelopeJson, IReadOnlyList<BowireProvisionedEnvironment> provisioned)
    {
        ArgumentNullException.ThrowIfNull(provisioned);
        if (provisioned.Count == 0 || string.IsNullOrWhiteSpace(envelopeJson)) return envelopeJson;

        JsonObject? envelope;
        try { envelope = JsonNode.Parse(envelopeJson) as JsonObject; }
        catch (JsonException) { return envelopeJson; }
        if (envelope is null) return envelopeJson;

        var environments = envelope["environments"] as JsonArray;
        if (environments is null)
        {
            environments = [];
            envelope["environments"] = environments;
        }

        // A stored entry with a host id can only be a leftover from before the
        // strip existed. The declaration wins, so drop it rather than showing
        // the same name twice with different values.
        var declaredIds = provisioned.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = environments.Count - 1; i >= 0; i--)
        {
            if (environments[i] is JsonObject stored
                && stored["id"]?.GetValue<string>() is { } id
                && declaredIds.Contains(id))
            {
                environments.RemoveAt(i);
            }
        }

        foreach (var env in provisioned)
        {
            var vars = new JsonObject();
            foreach (var (key, value) in env.Variables) vars[key] = value;

            environments.Add(new JsonObject
            {
                ["id"] = env.Id,
                ["name"] = env.Name,
                ["vars"] = vars,
                ["provisioned"] = true,
            });
        }

        return envelope.ToJsonString();
    }

    /// <summary>
    /// Remove host-declared environments from an envelope the workbench is
    /// saving.
    /// </summary>
    /// <remarks>
    /// Matched by id rather than by the <c>provisioned</c> flag: the flag is
    /// something a client sends and could omit, and the ids are ours. Anything
    /// else in the envelope — globals, the active id, the person's own
    /// environments — passes through untouched.
    /// </remarks>
    public static string Strip(string envelopeJson, IReadOnlyList<BowireProvisionedEnvironment> provisioned)
    {
        ArgumentNullException.ThrowIfNull(provisioned);
        if (provisioned.Count == 0 || string.IsNullOrWhiteSpace(envelopeJson)) return envelopeJson;

        JsonObject? envelope;
        try { envelope = JsonNode.Parse(envelopeJson) as JsonObject; }
        catch (JsonException) { return envelopeJson; }
        if (envelope is null) return envelopeJson;

        if (envelope["environments"] is not JsonArray environments) return envelopeJson;

        var declaredIds = provisioned.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var removed = false;
        for (var i = environments.Count - 1; i >= 0; i--)
        {
            if (environments[i] is JsonObject stored
                && stored["id"]?.GetValue<string>() is { } id
                && declaredIds.Contains(id))
            {
                environments.RemoveAt(i);
                removed = true;
            }
        }

        return removed ? envelope.ToJsonString() : envelopeJson;
    }
}
