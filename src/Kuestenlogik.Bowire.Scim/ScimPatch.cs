// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Scim;

/// <summary>A PATCH body this implementation will not apply.</summary>
public sealed class ScimPatchException : Exception
{
    /// <summary>An unusable PATCH document, unexplained.</summary>
    public ScimPatchException() { }

    /// <summary>An unusable PATCH document, with the reason a connector should log.</summary>
    public ScimPatchException(string message) : base(message) { }

    /// <summary>An unusable PATCH document, wrapping what actually failed.</summary>
    public ScimPatchException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>One entry of a PATCH request (RFC 7644 §3.5.2).</summary>
public sealed class ScimPatchOperation
{
    /// <summary><c>add</c>, <c>replace</c> or <c>remove</c>. Case-insensitive on the wire.</summary>
    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    /// <summary>Which attribute. Absent means the value is an object applied at the root.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>The new value.</summary>
    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }
}

/// <summary>A PATCH request (RFC 7644 §3.5.2).</summary>
public sealed class ScimPatchRequest
{
    /// <summary>Schema URNs this request conforms to.</summary>
    [JsonPropertyName("schemas")]
    public List<string> Schemas { get; init; } = [];

    /// <summary>The operations, applied in order.</summary>
    [JsonPropertyName("Operations")]
    public List<ScimPatchOperation> Operations { get; init; } = [];
}

/// <summary>
/// Applies a PATCH document to a resource (#96).
/// </summary>
/// <remarks>
/// <para>
/// PATCH is where SCIM implementations usually break, because the two
/// connectors that matter disagree about the shape. Okta sends a lower-case
/// <c>op</c> with an explicit <c>path</c>; Entra ID sends a capitalised
/// <c>Op</c> and, for deactivation, no path at all — the value is an object
/// whose members apply at the root. Both are legal. An implementation that
/// handles one of them deactivates half its users.
/// </para>
/// <para>
/// An attribute this code does not model is not dropped: it is kept in the
/// resource's extension bag, so the next GET returns what the connector sent.
/// A connector that reads back a resource missing an attribute it just wrote
/// concludes the write failed and retries, forever.
/// </para>
/// </remarks>
public static class ScimPatch
{
    /// <summary>Apply <paramref name="request"/> to a user.</summary>
    /// <exception cref="ScimPatchException">The document uses something unsupported.</exception>
    public static void Apply(ScimUser user, ScimPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        foreach (var operation in request.Operations)
        {
            var op = Normalise(operation.Op);

            if (string.IsNullOrWhiteSpace(operation.Path))
            {
                // Entra ID's deactivation: no path, the value is the change.
                ApplyRootObject(user, operation, op);
                continue;
            }

            ApplyUserPath(user, operation.Path.Trim(), op, operation.Value);
        }
    }

    /// <summary>Apply <paramref name="request"/> to a group.</summary>
    /// <exception cref="ScimPatchException">The document uses something unsupported.</exception>
    public static void Apply(ScimGroup group, ScimPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(request);

        foreach (var operation in request.Operations)
        {
            var op = Normalise(operation.Op);
            var path = operation.Path?.Trim();

            if (string.IsNullOrWhiteSpace(path))
            {
                if (operation.Value is not { ValueKind: JsonValueKind.Object } root) continue;
                foreach (var member in root.EnumerateObject())
                {
                    ApplyGroupPath(group, member.Name, op, member.Value);
                }
                continue;
            }

            ApplyGroupPath(group, path, op, operation.Value);
        }
    }

    // ---- users ----

    private static void ApplyRootObject(ScimUser user, ScimPatchOperation operation, string op)
    {
        if (operation.Value is not { ValueKind: JsonValueKind.Object } root)
        {
            throw new ScimPatchException(
                "An operation without a path must carry an object value naming the attributes to change.");
        }

        foreach (var member in root.EnumerateObject())
        {
            ApplyUserPath(user, member.Name, op, member.Value);
        }
    }

    private static void ApplyUserPath(ScimUser user, string path, string op, JsonElement? value)
    {
        var removing = op == "remove";

        switch (path.ToUpperInvariant())
        {
            case "ACTIVE":
                user.Active = !removing && AsBool(value, path);
                return;

            case "USERNAME":
                user.UserName = removing ? "" : AsString(value, path) ?? user.UserName;
                return;

            case "EXTERNALID":
                user.ExternalId = removing ? null : AsString(value, path);
                return;

            case "DISPLAYNAME":
                user.DisplayName = removing ? null : AsString(value, path);
                return;

            case "NAME.GIVENNAME":
                (user.Name ??= new ScimName()).GivenName = removing ? null : AsString(value, path);
                return;

            case "NAME.FAMILYNAME":
                (user.Name ??= new ScimName()).FamilyName = removing ? null : AsString(value, path);
                return;

            case "NAME.FORMATTED":
                (user.Name ??= new ScimName()).Formatted = removing ? null : AsString(value, path);
                return;

            case "NAME":
                user.Name = removing ? null : Read<ScimName>(value);
                return;

            case "EMAILS":
                // Replaced in place: the property is init-only so the caller's
                // list stays the one instance everything else already holds.
                user.Emails.Clear();
                if (!removing) user.Emails.AddRange(ReadValues(value));
                return;

            default:
                // Unmodelled but not unwanted: keeping it means the connector
                // reads back what it wrote and stops retrying.
                Remember(user, path, op, value);
                return;
        }
    }

    private static void Remember(ScimUser user, string path, string op, JsonElement? value)
    {
        if (op == "remove" || value is not { } element)
        {
            user.Extensions.Remove(path);
            return;
        }

        user.Extensions[path] = element.Clone();
    }

    // ---- groups ----

    private static void ApplyGroupPath(ScimGroup group, string path, string op, JsonElement? value)
    {
        switch (path.ToUpperInvariant())
        {
            case "DISPLAYNAME":
                group.DisplayName = op == "remove" ? "" : AsString(value, path) ?? group.DisplayName;
                return;

            case "EXTERNALID":
                group.ExternalId = op == "remove" ? null : AsString(value, path);
                return;

            case "MEMBERS":
                ApplyMembers(group, op, value);
                return;

            default:
                // A group has no extension bag, and quietly accepting an
                // attribute that will never be stored would report success for
                // a change that did not happen.
                throw new ScimPatchException(
                    $"'{path}' is not an attribute of a Group that this service provider stores.");
        }
    }

    private static void ApplyMembers(ScimGroup group, string op, JsonElement? value)
    {
        var incoming = ReadValues(value);

        switch (op)
        {
            case "add":
                foreach (var member in incoming)
                {
                    if (member.Value is null) continue;
                    if (group.Members.Any(m => string.Equals(m.Value, member.Value, StringComparison.Ordinal)))
                    {
                        continue;
                    }
                    group.Members.Add(member);
                }
                return;

            case "remove":
                // A remove with no value clears the attribute (RFC 7644
                // §3.5.2.2); with one, it takes out just those members.
                if (incoming.Count == 0) { group.Members.Clear(); return; }
                foreach (var member in incoming)
                {
                    var doomed = group.Members
                        .Where(m => string.Equals(m.Value, member.Value, StringComparison.Ordinal))
                        .ToList();
                    foreach (var entry in doomed) group.Members.Remove(entry);
                }
                return;

            default:
                group.Members.Clear();
                group.Members.AddRange(incoming);
                return;
        }
    }

    // ---- reading values ----

    private static string Normalise(string op)
    {
        var normalised = (op ?? "").Trim().ToUpperInvariant() switch
        {
            "ADD" => "add",
            "REPLACE" => "replace",
            "REMOVE" => "remove",
            _ => "",
        };

        if (normalised.Length == 0)
        {
            throw new ScimPatchException(
                $"'{op}' is not a PATCH operation. Expected add, replace or remove.");
        }

        return normalised;
    }

    private static bool AsBool(JsonElement? value, string path) => value switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        // Some connectors send booleans as strings. Accepting that is not
        // laxity: refusing it would leave the user active after a
        // deactivation the IdP believes succeeded.
        { ValueKind: JsonValueKind.String } s when bool.TryParse(s.GetString(), out var parsed) => parsed,
        _ => throw new ScimPatchException($"'{path}' expects true or false."),
    };

    private static string? AsString(JsonElement? value, string path) => value switch
    {
        null or { ValueKind: JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } v => v.ToString(),
        _ => throw new ScimPatchException($"'{path}' expects a string."),
    };

    private static T? Read<T>(JsonElement? value) where T : class
        => value is { ValueKind: not JsonValueKind.Null } element
            ? element.Deserialize<T>()
            : null;

    private static List<ScimValue> ReadValues(JsonElement? value) => value switch
    {
        null or { ValueKind: JsonValueKind.Null } => [],
        { ValueKind: JsonValueKind.Array } array => array.Deserialize<List<ScimValue>>() ?? [],
        { ValueKind: JsonValueKind.Object } single =>
            single.Deserialize<ScimValue>() is { } one ? [one] : [],
        _ => [],
    };
}
