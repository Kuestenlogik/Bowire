// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.Scim;

/// <summary>
/// Records what an identity provider's connector actually did, one line of
/// JSON per SCIM request (#639).
/// </summary>
/// <remarks>
/// <para>
/// <c>scim/events.jsonl</c> already records every mutation, but it records
/// the <em>outcome</em> — action, id, name, active. That answers "what is
/// the state now", and #639 asks something else: what does a real connector
/// do on the wire. Those questions have no overlap. The event log is written
/// only from mutations, so a provider's reads — the paging walk, the
/// existence filter, the query it uses to decide whether a deactivated user
/// still exists — leave no trace at all, and they are most of what a live
/// round-trip is for.
/// </para>
/// <para>
/// So this records the request, not the result: method, path and query
/// (paging and filters become visible), the status returned, how long it
/// took, and for PATCH the dialect the connector actually used. Okta sends
/// lower-case <c>op</c> with a <c>path</c>; Entra ID capitalises <c>Op</c>
/// and for deactivation omits the path. Bowire accepts both, and until
/// something writes down which one arrived, "we handle both" stays a claim
/// about a fixture.
/// </para>
/// <para>
/// It also names the attributes a payload carried that Bowire does not
/// model. Those are kept verbatim and handed back on the next <c>GET</c>,
/// which is correct and completely silent — an unmodelled attribute that
/// matters would otherwise be discovered by a customer, not by us.
/// </para>
/// <para>
/// <b>Off by default.</b> This writes what the connector sent: user names,
/// e-mail addresses, the filters a directory walk used. That is personal
/// data about people who never agreed to be in a debug file, and it is
/// wanted for a bounded exercise — a provisioning round-trip — not for
/// normal operation. Turning it on is a decision, not a default.
/// </para>
/// </remarks>
public sealed class ScimProvisioningTrace
{
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private static readonly JsonSerializerOptions s_json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Create a trace writing to <paramref name="path"/>.</summary>
    /// <remarks>
    /// The path is resolved here rather than on first write, so a
    /// configuration mistake surfaces at startup where an operator can act on
    /// it — instead of silently dropping every line of the exercise it was
    /// turned on for.
    /// </remarks>
    public ScimProvisioningTrace(string path, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Where the lines are written.
    /// </summary>
    /// <remarks>
    /// Named <c>FilePath</c> rather than <c>Path</c> so the JSON member name
    /// <c>"Path"</c> below — which is Entra's capitalisation of a PATCH
    /// operation's path, and has nothing to do with this property — is not
    /// read as a stale reference to it.
    /// </remarks>
    public string FilePath => _path;

    /// <summary>
    /// Record one request.
    /// </summary>
    /// <remarks>
    /// Best-effort, like the event log it sits beside: losing a trace line
    /// must never fail the call the connector is waiting on, because a
    /// provider that sees a 500 retries the whole sync.
    /// </remarks>
    public void Record(
        string method,
        string path,
        string? query,
        int status,
        double elapsedMs,
        string? patchDialect = null,
        IReadOnlyCollection<string>? unmodelledAttributes = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                at = _clock.GetUtcNow(),
                method,
                path,
                query = string.IsNullOrEmpty(query) ? null : query,
                status,
                ms = Math.Round(elapsedMs, 1),
                patchDialect,
                unmodelled = unmodelledAttributes is { Count: > 0 }
                    ? unmodelledAttributes
                    : null,
            }, s_json);

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // One writer at a time: ASP.NET Core serves SCIM requests
            // concurrently, and a connector's initial sync is the one moment
            // this file is written from several threads at once. Interleaved
            // half-lines would corrupt exactly the evidence being collected.
            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException)
        {
            // The narrow pair was not enough, and a test found it: a path the
            // filesystem rejects throws ArgumentException, which would have
            // escaped and failed the provisioning call the connector is
            // waiting on — the one thing this must never do, since a failed
            // sync retries forever. The constructor now catches the ordinary
            // case at startup; this is the rest.
            _ = ex;
        }
    }

    /// <summary>
    /// Which connector's PATCH shape this document is, or <c>null</c> when it
    /// is not a PATCH or cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported as observed, never guessed: the two shapes are told apart by
    /// what is actually on the wire — the casing of the <c>op</c> member and
    /// whether a <c>path</c> is present — not by a User-Agent, which a proxy
    /// may rewrite and a test harness will not send at all.
    /// </para>
    /// <para>
    /// A document that matches neither is reported as <c>other</c> rather
    /// than forced into one of them. That is the interesting case: it means
    /// a third connector, or a version that changed its mind, and calling it
    /// Okta would bury exactly the finding this exists to surface.
    /// </para>
    /// </remarks>
    public static string? DetectPatchDialect(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("Operations", out var ops)
                && !doc.RootElement.TryGetProperty("operations", out ops))
            {
                return null;
            }
            if (ops.ValueKind != JsonValueKind.Array) return null;

            var sawLowerOp = false;
            var sawUpperOp = false;
            var sawPath = false;
            var sawPathless = false;

            foreach (var op in ops.EnumerateArray())
            {
                if (op.ValueKind != JsonValueKind.Object) continue;
                if (op.TryGetProperty("op", out _)) sawLowerOp = true;
                if (op.TryGetProperty("Op", out _)) sawUpperOp = true;
                if (op.TryGetProperty("path", out _) || op.TryGetProperty("Path", out _)) sawPath = true;
                else sawPathless = true;
            }

            if (sawUpperOp && !sawLowerOp) return "entra";
            if (sawLowerOp && !sawUpperOp) return sawPathless && !sawPath ? "entra-shaped" : "okta";
            return "other";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Members of a payload that Bowire does not model, in the order they
    /// appear.
    /// </summary>
    /// <remarks>
    /// Anything outside the core User and Group attributes — the Enterprise
    /// User extension, whatever a directory maps on top. Bowire keeps them
    /// verbatim and returns them, which is right and gives no signal; this
    /// is the signal.
    /// </remarks>
    public static IReadOnlyCollection<string> UnmodelledAttributes(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];

            var found = new List<string>();
            foreach (var member in doc.RootElement.EnumerateObject())
            {
                if (!s_modelled.Contains(member.Name)) found.Add(member.Name);
            }
            return found;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The members Bowire understands. Kept next to the resource types it
    /// mirrors — a name added there and forgotten here shows up as a false
    /// "unmodelled", which is noisy but never silent.
    /// </summary>
    private static readonly HashSet<string> s_modelled = new(StringComparer.Ordinal)
    {
        // Common
        "schemas", "id", "externalId", "meta",
        // User
        "userName", "name", "displayName", "active", "emails", "groups",
        // Group
        "members",
        // PATCH envelope
        "Operations", "operations",
    };
}
