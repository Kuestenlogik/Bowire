// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Scim;

/// <summary>
/// The SCIM 2.0 wire surface (RFC 7644) — everything an identity provider's
/// provisioning connector calls (#96).
/// </summary>
internal static class BowireScimEndpoints
{
    /// <summary>The media type SCIM defines. Connectors send and expect it.</summary>
    public const string ContentType = "application/scim+json";

    private static readonly JsonSerializerOptions s_json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder Map(
        IEndpointRouteBuilder endpoints, BowireScimOptions options, BowireScimStore store)
    {
        var basePath = options.BasePath.TrimEnd('/');

        // ---- discovery (RFC 7643 §5) ----
        // Every connector reads these before it writes anything; Entra ID
        // refuses to start a sync without ServiceProviderConfig.

        endpoints.MapGet($"{basePath}/ServiceProviderConfig", (HttpContext http) =>
            Guard(http, options) ?? Ok(ServiceProviderConfig(options)));

        endpoints.MapGet($"{basePath}/ResourceTypes", (HttpContext http) =>
            Guard(http, options) ?? Ok(ResourceTypes(basePath)));

        endpoints.MapGet($"{basePath}/Schemas", (HttpContext http) =>
            Guard(http, options) ?? Ok(Schemas()));

        // ---- users ----

        endpoints.MapGet($"{basePath}/Users", (HttpContext http) =>
            Guard(http, options) ?? ListUsers(http, options, store, basePath));

        endpoints.MapGet($"{basePath}/Users/{{id}}", (HttpContext http, string id) =>
        {
            var refused = Guard(http, options);
            if (refused is not null) return refused;

            var record = store.GetUser(id);
            return record is null
                ? NotFound(id)
                : Ok(Located(record.Resource, http, basePath));
        });

        endpoints.MapPost($"{basePath}/Users", async (HttpContext http) =>
        {
            var refused = Guard(http, options);
            if (refused is not null) return refused;

            var resource = await ReadAsync<ScimUser>(http).ConfigureAwait(false);
            if (resource is null) return Malformed("The request body is not a SCIM User.");

            try
            {
                var created = store.CreateUser(resource);
                return Ok(Located(created.Resource, http, basePath), StatusCodes.Status201Created);
            }
            catch (ScimConflictException ex)
            {
                return Conflict(ex.Message);
            }
        });

        endpoints.MapPut($"{basePath}/Users/{{id}}", async (HttpContext http, string id) =>
        {
            var refused = Guard(http, options);
            if (refused is not null) return refused;

            var resource = await ReadAsync<ScimUser>(http).ConfigureAwait(false);
            if (resource is null) return Malformed("The request body is not a SCIM User.");

            try
            {
                var replaced = store.ReplaceUser(id, resource);
                return replaced is null ? NotFound(id) : Ok(Located(replaced.Resource, http, basePath));
            }
            catch (ScimConflictException ex)
            {
                return Conflict(ex.Message);
            }
        });

        endpoints.MapPatch($"{basePath}/Users/{{id}}", async (HttpContext http, string id) =>
        {
            var refused = Guard(http, options);
            if (refused is not null) return refused;

            var patch = await ReadAsync<ScimPatchRequest>(http).ConfigureAwait(false);
            if (patch is null) return Malformed("The request body is not a SCIM PATCH document.");

            try
            {
                var updated = store.UpdateUser(id, user => ScimPatch.Apply(user, patch));
                return updated is null ? NotFound(id) : Ok(Located(updated.Resource, http, basePath));
            }
            catch (ScimPatchException ex)
            {
                return Error(StatusCodes.Status400BadRequest, "invalidValue", ex.Message);
            }
            catch (ScimConflictException ex)
            {
                return Conflict(ex.Message);
            }
        });

        endpoints.MapDelete($"{basePath}/Users/{{id}}", (HttpContext http, string id) =>
            Guard(http, options)
                ?? (store.DeleteUser(id) ? Results.NoContent() : NotFound(id)));

        // ---- groups ----

        endpoints.MapGet($"{basePath}/Groups", (HttpContext http) =>
            Guard(http, options) ?? ListGroups(http, options, store, basePath));

        endpoints.MapGet($"{basePath}/Groups/{{id}}", (HttpContext http, string id) =>
        {
            var refused = Guard(http, options);
            if (refused is not null) return refused;

            var group = store.GetGroup(id);
            return group is null ? NotFound(id) : Ok(Located(group, http, basePath));
        });

        endpoints.MapPost($"{basePath}/Groups", async (HttpContext http) =>
        {
            var refused = Guard(http, options);
            if (refused is not null) return refused;

            var group = await ReadAsync<ScimGroup>(http).ConfigureAwait(false);
            if (group is null) return Malformed("The request body is not a SCIM Group.");

            try
            {
                var created = store.CreateGroup(group);
                return Ok(Located(created, http, basePath), StatusCodes.Status201Created);
            }
            catch (ScimConflictException ex)
            {
                return Conflict(ex.Message);
            }
        });

        endpoints.MapPut($"{basePath}/Groups/{{id}}", async (HttpContext http, string id) =>
        {
            var refused = Guard(http, options);
            if (refused is not null) return refused;

            var group = await ReadAsync<ScimGroup>(http).ConfigureAwait(false);
            if (group is null) return Malformed("The request body is not a SCIM Group.");

            try
            {
                var replaced = store.ReplaceGroup(id, group);
                return replaced is null ? NotFound(id) : Ok(Located(replaced, http, basePath));
            }
            catch (ScimConflictException ex)
            {
                return Conflict(ex.Message);
            }
        });

        endpoints.MapPatch($"{basePath}/Groups/{{id}}", async (HttpContext http, string id) =>
        {
            var refused = Guard(http, options);
            if (refused is not null) return refused;

            var patch = await ReadAsync<ScimPatchRequest>(http).ConfigureAwait(false);
            if (patch is null) return Malformed("The request body is not a SCIM PATCH document.");

            try
            {
                var updated = store.UpdateGroup(id, group => ScimPatch.Apply(group, patch));
                return updated is null ? NotFound(id) : Ok(Located(updated, http, basePath));
            }
            catch (ScimPatchException ex)
            {
                return Error(StatusCodes.Status400BadRequest, "invalidValue", ex.Message);
            }
        });

        endpoints.MapDelete($"{basePath}/Groups/{{id}}", (HttpContext http, string id) =>
            Guard(http, options)
                ?? (store.DeleteGroup(id) ? Results.NoContent() : NotFound(id)));

        return endpoints;
    }

    // ---- listing ----

    private static IResult ListUsers(
        HttpContext http, BowireScimOptions options, BowireScimStore store, string basePath)
    {
        ScimFilter? filter;
        try { filter = ParseFilter(http); }
        catch (ScimFilterException ex)
        {
            return Error(StatusCodes.Status400BadRequest, "invalidFilter", ex.Message);
        }

        // #96 — filter, page, *then* stamp the location. Stamping first meant
        // a directory of 10 000 built 10 000 absolute URLs to return fifty of
        // them, and wrote every one of them into the stored resource: the
        // work and the write both scaled with the directory rather than with
        // the page.
        var matching = store.Users()
            .Where(r => filter is null || filter.Matches(name => UserAttribute(r.Resource, name)))
            .Select(r => r.Resource)
            .ToList();

        return Ok(Page(matching, u => Located(u, http, basePath), http, options));
    }

    private static IResult ListGroups(
        HttpContext http, BowireScimOptions options, BowireScimStore store, string basePath)
    {
        ScimFilter? filter;
        try { filter = ParseFilter(http); }
        catch (ScimFilterException ex)
        {
            return Error(StatusCodes.Status400BadRequest, "invalidFilter", ex.Message);
        }

        var matching = store.Groups()
            .Where(g => filter is null || filter.Matches(name => GroupAttribute(g, name)))
            .ToList();

        return Ok(Page(matching, g => Located(g, http, basePath), http, options));
    }

    private static ScimFilter? ParseFilter(HttpContext http)
    {
        var text = http.Request.Query["filter"].ToString();
        return string.IsNullOrWhiteSpace(text) ? null : ScimFilter.Parse(text);
    }

    /// <summary>
    /// One page of <paramref name="all"/>, with <paramref name="project"/>
    /// applied to the slice rather than to the whole list.
    /// </summary>
    /// <remarks>
    /// The projection is deferred on purpose: it is what stamps the absolute
    /// location onto a resource, and doing that to every match in order to
    /// return one page made both the cost and the write scale with the size
    /// of the directory (#96).
    /// </remarks>
    private static ScimListResponse<T> Page<TSource, T>(
        IReadOnlyList<TSource> all,
        Func<TSource, T> project,
        HttpContext http,
        BowireScimOptions options)
    {
        // 1-based, per RFC 7644 §3.4.2.4 — a connector that asks for
        // startIndex=1 and gets the second resource silently skips the first
        // user in the directory.
        var start = Math.Max(1, Number(http, "startIndex", 1));
        var count = Math.Clamp(
            Number(http, "count", options.DefaultPageSize), 0, options.MaxPageSize);

        var page = all.Skip(start - 1).Take(count).Select(project).ToList();

        return new ScimListResponse<T>
        {
            TotalResults = all.Count,
            StartIndex = start,
            ItemsPerPage = page.Count,
            Resources = page,
        };
    }

    private static int Number(HttpContext http, string key, int fallback)
        => int.TryParse(http.Request.Query[key].ToString(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;

    private static string? UserAttribute(ScimUser user, string name) => name.ToUpperInvariant() switch
    {
        "ID" => user.Id,
        "USERNAME" => user.UserName,
        "EXTERNALID" => user.ExternalId,
        "DISPLAYNAME" => user.DisplayName,
        "ACTIVE" => user.Active ? "true" : "false",
        _ => null,
    };

    private static string? GroupAttribute(ScimGroup group, string name) => name.ToUpperInvariant() switch
    {
        "ID" => group.Id,
        "DISPLAYNAME" => group.DisplayName,
        "EXTERNALID" => group.ExternalId,
        _ => null,
    };

    // ---- auth ----

    /// <summary>
    /// Refuse the request when the bearer token is missing or wrong, otherwise
    /// <c>null</c>.
    /// </summary>
    private static IResult? Guard(HttpContext http, BowireScimOptions options)
    {
        var presented = http.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";

        if (!presented.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return Error(StatusCodes.Status401Unauthorized, "invalidCredentials",
                "This endpoint requires a bearer token.");
        }

        if (!Matches(presented[scheme.Length..].Trim(), options.Token))
        {
            return Error(StatusCodes.Status401Unauthorized, "invalidCredentials",
                "The bearer token is not the one this service provider was configured with.");
        }

        return null;
    }

    /// <summary>
    /// Compare in time that does not depend on how much of the token is right.
    /// </summary>
    /// <remarks>
    /// An ordinary string comparison returns as soon as two characters differ,
    /// and the provisioning endpoint is reachable by anyone who can route to
    /// the host — which is the setting where a timing side-channel is worth
    /// something to an attacker who can send a great many requests.
    /// </remarks>
    private static bool Matches(string presented, string? configured)
    {
        if (string.IsNullOrEmpty(configured)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(configured));
    }

    // ---- bodies and responses ----

    /// <summary>
    /// Read a SCIM body regardless of the media type it was sent with.
    /// </summary>
    /// <remarks>
    /// Connectors send <c>application/scim+json</c>, which ASP.NET's JSON
    /// binding does not recognise — <c>ReadFromJsonAsync</c> answers 415 and
    /// the sync fails before a single user is created. Reading the stream
    /// directly is what makes the endpoint work with the clients it exists
    /// for.
    /// </remarks>
    private static async Task<T?> ReadAsync<T>(HttpContext http) where T : class
    {
        try
        {
            return await JsonSerializer
                .DeserializeAsync<T>(http.Request.Body, s_json, http.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException) { return null; }
    }

    private static IResult Ok(object payload, int status = StatusCodes.Status200OK)
        => Results.Json(payload, s_json, ContentType, status);

    private static IResult Error(int status, string? scimType, string detail)
        => Results.Json(
            new ScimError
            {
                Status = status.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ScimType = scimType,
                Detail = detail,
            },
            s_json, ContentType, status);

    private static IResult NotFound(string id)
        => Error(StatusCodes.Status404NotFound, null, $"No resource with id '{id}'.");

    private static IResult Conflict(string detail)
        => Error(StatusCodes.Status409Conflict, "uniqueness", detail);

    private static IResult Malformed(string detail)
        => Error(StatusCodes.Status400BadRequest, "invalidSyntax", detail);

    // ---- shaping ----

    private static ScimUser Located(ScimUser user, HttpContext http, string basePath)
    {
        user.Meta.Location = Absolute(http, $"{basePath}/Users/{user.Id}");
        return user;
    }

    private static ScimGroup Located(ScimGroup group, HttpContext http, string basePath)
    {
        group.Meta.Location = Absolute(http, $"{basePath}/Groups/{group.Id}");
        return group;
    }

    private static string Absolute(HttpContext http, string path)
        => $"{http.Request.Scheme}://{http.Request.Host}{path}";

    // ---- discovery documents ----

    private static object ServiceProviderConfig(BowireScimOptions options) => new
    {
        schemas = new[] { ScimSchemas.ServiceProviderConfig },
        documentationUri = "https://bowire.io/docs/setup/multi-user",
        patch = new { supported = true },
        // Not implemented, and said so. A connector that is told bulk is
        // supported and then gets a 404 retries the whole sync.
        bulk = new { supported = false, maxOperations = 0, maxPayloadSize = 0 },
        filter = new { supported = true, maxResults = options.MaxPageSize },
        changePassword = new { supported = false },
        sort = new { supported = false },
        etag = new { supported = false },
        authenticationSchemes = new[]
        {
            new
            {
                type = "oauthbearertoken",
                name = "OAuth Bearer Token",
                description = "A shared bearer token configured on the Bowire host as Bowire:Scim:Token.",
                primary = true,
            },
        },
    };

    private static object ResourceTypes(string basePath) => new
    {
        schemas = new[] { ScimSchemas.ListResponse },
        totalResults = 2,
        startIndex = 1,
        itemsPerPage = 2,
        Resources = new object[]
        {
            new
            {
                schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ResourceType" },
                id = "User",
                name = "User",
                endpoint = $"{basePath}/Users",
                description = "A Bowire identity.",
                schema = ScimSchemas.User,
                schemaExtensions = new[]
                {
                    new { schema = ScimSchemas.EnterpriseUser, required = false },
                },
            },
            new
            {
                schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ResourceType" },
                id = "Group",
                name = "Group",
                endpoint = $"{basePath}/Groups",
                description = "A set of Bowire identities.",
                schema = ScimSchemas.Group,
            },
        },
    };

    private static object Schemas() => new
    {
        schemas = new[] { ScimSchemas.ListResponse },
        totalResults = 2,
        startIndex = 1,
        itemsPerPage = 2,
        Resources = new object[]
        {
            new
            {
                id = ScimSchemas.User,
                name = "User",
                description = "SCIM core User (RFC 7643 §4.1).",
                attributes = new object[]
                {
                    Attribute("userName", "string", required: true, unique: "server"),
                    Attribute("externalId", "string"),
                    Attribute("displayName", "string"),
                    Attribute("active", "boolean"),
                    Attribute("emails", "complex", multiValued: true),
                    Attribute("name", "complex"),
                },
            },
            new
            {
                id = ScimSchemas.Group,
                name = "Group",
                description = "SCIM core Group (RFC 7643 §4.2).",
                attributes = new object[]
                {
                    Attribute("displayName", "string", required: true, unique: "server"),
                    Attribute("externalId", "string"),
                    Attribute("members", "complex", multiValued: true),
                },
            },
        },
    };

    private static object Attribute(
        string name, string type, bool required = false, bool multiValued = false, string unique = "none")
        => new
        {
            name,
            type,
            multiValued,
            required,
            caseExact = false,
            mutability = "readWrite",
            returned = "default",
            uniqueness = unique,
        };
}
