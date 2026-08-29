// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Scim;

/// <summary>The schema URNs this implementation names on the wire (RFC 7643).</summary>
public static class ScimSchemas
{
    /// <summary>Core User resource.</summary>
    public const string User = "urn:ietf:params:scim:schemas:core:2.0:User";

    /// <summary>Core Group resource.</summary>
    public const string Group = "urn:ietf:params:scim:schemas:core:2.0:Group";

    /// <summary>Multi-resource query response (RFC 7644 §3.4.2).</summary>
    public const string ListResponse = "urn:ietf:params:scim:api:messages:2.0:ListResponse";

    /// <summary>Error response (RFC 7644 §3.12).</summary>
    public const string Error = "urn:ietf:params:scim:api:messages:2.0:Error";

    /// <summary>PATCH request body (RFC 7644 §3.5.2).</summary>
    public const string PatchOp = "urn:ietf:params:scim:api:messages:2.0:PatchOp";

    /// <summary>Service provider configuration (RFC 7643 §5).</summary>
    public const string ServiceProviderConfig =
        "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig";

    /// <summary>
    /// Enterprise User extension. Advertised because Entra ID's default
    /// attribute mapping includes it; the attributes are stored and returned
    /// verbatim rather than interpreted, which is all a provisioning
    /// round-trip needs.
    /// </summary>
    public const string EnterpriseUser =
        "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";
}

/// <summary>Resource metadata (RFC 7643 §3.1).</summary>
public sealed class ScimMeta
{
    /// <summary><c>User</c> or <c>Group</c>.</summary>
    [JsonPropertyName("resourceType")]
    public string ResourceType { get; set; } = "";

    /// <summary>When the resource was provisioned.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; }

    /// <summary>When it last changed.</summary>
    [JsonPropertyName("lastModified")]
    public DateTimeOffset LastModified { get; set; }

    /// <summary>Absolute URI of this resource. Filled in per response.</summary>
    [JsonPropertyName("location")]
    public string? Location { get; set; }

    /// <summary>Entity tag, for the conditional requests some connectors send.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary>A user's name (RFC 7643 §4.1.1).</summary>
public sealed class ScimName
{
    /// <summary>The full name as the IdP renders it.</summary>
    [JsonPropertyName("formatted")]
    public string? Formatted { get; set; }

    /// <summary>Family name.</summary>
    [JsonPropertyName("familyName")]
    public string? FamilyName { get; set; }

    /// <summary>Given name.</summary>
    [JsonPropertyName("givenName")]
    public string? GivenName { get; set; }
}

/// <summary>A typed, optionally primary value — e-mail, phone (RFC 7643 §2.4).</summary>
public sealed class ScimValue
{
    /// <summary>The value itself.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>Its type, e.g. <c>work</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Whether this is the preferred entry of its kind.</summary>
    [JsonPropertyName("primary")]
    public bool? Primary { get; set; }

    /// <summary>Human-readable label. Groups use this for member names.</summary>
    [JsonPropertyName("display")]
    public string? Display { get; set; }
}

/// <summary>
/// A provisioned identity (RFC 7643 §4.1).
/// </summary>
/// <remarks>
/// <para>
/// The subset an IdP actually sends, kept deliberately narrow. Every
/// attribute here has a consumer: <see cref="UserName"/> and
/// <see cref="ExternalId"/> are how a token gets matched to a record,
/// <see cref="Active"/> is what deprovisioning flips, and the rest is what a
/// connector reads back to decide the round-trip succeeded.
/// </para>
/// <para>
/// Attributes outside this set are preserved in
/// <see cref="Extensions"/> rather than dropped. A connector that sends the
/// Enterprise User extension and reads back a record without it treats the
/// provisioning as failed, and inventing a field for every mapping an
/// operator might configure is not a thing that finishes.
/// </para>
/// </remarks>
public sealed class ScimUser
{
    /// <summary>Schema URNs this resource conforms to.</summary>
    [JsonPropertyName("schemas")]
    public List<string> Schemas { get; init; } = [ScimSchemas.User];

    /// <summary>Server-assigned, immutable, opaque.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>The IdP's own identifier for this person.</summary>
    [JsonPropertyName("externalId")]
    public string? ExternalId { get; set; }

    /// <summary>The login name. Unique across the service provider, case-insensitively.</summary>
    [JsonPropertyName("userName")]
    public string UserName { get; set; } = "";

    /// <summary>Structured name.</summary>
    [JsonPropertyName("name")]
    public ScimName? Name { get; set; }

    /// <summary>Name to show in a UI.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>E-mail addresses.</summary>
    [JsonPropertyName("emails")]
    public List<ScimValue> Emails { get; init; } = [];

    /// <summary>
    /// Whether the identity may use Bowire. This is the attribute
    /// deprovisioning flips, and the one thing that has to be enforced rather
    /// than merely recorded.
    /// </summary>
    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;

    /// <summary>Groups this user belongs to. Read-only per RFC 7643 §4.1.2.</summary>
    [JsonPropertyName("groups")]
    public List<ScimValue> Groups { get; init; } = [];

    /// <summary>Resource metadata.</summary>
    [JsonPropertyName("meta")]
    public ScimMeta Meta { get; set; } = new() { ResourceType = "User" };

    /// <summary>
    /// Everything the connector sent that this implementation does not model,
    /// kept verbatim so it survives a round-trip.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement> Extensions { get; init; }
        = new(StringComparer.Ordinal);
}

/// <summary>A provisioned group (RFC 7643 §4.2).</summary>
public sealed class ScimGroup
{
    /// <summary>Schema URNs this resource conforms to.</summary>
    [JsonPropertyName("schemas")]
    public List<string> Schemas { get; init; } = [ScimSchemas.Group];

    /// <summary>Server-assigned, immutable, opaque.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>The IdP's own identifier for this group.</summary>
    [JsonPropertyName("externalId")]
    public string? ExternalId { get; set; }

    /// <summary>The group's name. Unique, case-insensitively.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    /// <summary>Member user ids.</summary>
    [JsonPropertyName("members")]
    public List<ScimValue> Members { get; init; } = [];

    /// <summary>Resource metadata.</summary>
    [JsonPropertyName("meta")]
    public ScimMeta Meta { get; set; } = new() { ResourceType = "Group" };
}

/// <summary>A page of resources (RFC 7644 §3.4.2).</summary>
/// <typeparam name="T">The resource type being listed.</typeparam>
public sealed class ScimListResponse<T>
{
    /// <summary>Schema URNs this response conforms to.</summary>
    [JsonPropertyName("schemas")]
    public List<string> Schemas { get; init; } = [ScimSchemas.ListResponse];

    /// <summary>How many resources match, before paging.</summary>
    [JsonPropertyName("totalResults")]
    public int TotalResults { get; set; }

    /// <summary>1-based index of the first resource returned.</summary>
    [JsonPropertyName("startIndex")]
    public int StartIndex { get; set; } = 1;

    /// <summary>How many are in this page.</summary>
    [JsonPropertyName("itemsPerPage")]
    public int ItemsPerPage { get; set; }

    /// <summary>The page itself. Capital R per RFC 7644 §3.4.2.</summary>
    [JsonPropertyName("Resources")]
    public List<T> Resources { get; init; } = [];
}

/// <summary>An error response (RFC 7644 §3.12).</summary>
public sealed class ScimError
{
    /// <summary>Schema URNs this response conforms to.</summary>
    [JsonPropertyName("schemas")]
    public List<string> Schemas { get; init; } = [ScimSchemas.Error];

    /// <summary>HTTP status, as a string — the RFC is explicit about the type.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    /// <summary>The SCIM error keyword, e.g. <c>uniqueness</c>, <c>invalidFilter</c>.</summary>
    [JsonPropertyName("scimType")]
    public string? ScimType { get; set; }

    /// <summary>What went wrong, for a human reading the connector's log.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}
