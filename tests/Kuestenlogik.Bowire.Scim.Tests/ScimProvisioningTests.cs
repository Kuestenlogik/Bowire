// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Scim.Tests;

/// <summary>
/// The wire behaviour a provisioning connector depends on (#96), driven
/// through the real endpoints.
/// </summary>
/// <remarks>
/// These are the tests that catch the failures an implementation cannot see
/// from the inside: the media type connectors actually send, the pagination
/// base, and an error body shaped so the connector can read what went wrong.
/// </remarks>
public sealed class ScimProvisioningTests : IAsyncLifetime
{
    private const string Token = "s3cr3t-provisioning-token";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-scim-http-" + Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new BowireScimOptions { Enabled = true, Token = Token });
        builder.Services.AddSingleton(new BowireScimStore(_root));

        _app = builder.Build();
        _app.MapBowireScim();
        await _app.StartAsync(TestContext.Current.CancellationToken);

        _client = _app.GetTestServer().CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- plumbing ----

    private static Uri Url(string path) => new(path, UriKind.Relative);

    /// <summary>A request body sent the way a connector sends it.</summary>
    private static StringContent Scim(string json)
        => new(json, Encoding.UTF8, BowireScimEndpoints.ContentType);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private Task<HttpResponseMessage> Get(string path)
        => _client.GetAsync(Url(path), TestContext.Current.CancellationToken);

    private async Task<HttpResponseMessage> Post(string path, string json)
    {
        using var content = Scim(json);
        return await _client.PostAsync(Url(path), content, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> Put(string path, string json)
    {
        using var content = Scim(json);
        return await _client.PutAsync(Url(path), content, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> Patch(string path, string json)
    {
        using var content = Scim(json);
        return await _client.PatchAsync(Url(path), content, TestContext.Current.CancellationToken);
    }

    private Task<HttpResponseMessage> Delete(string path)
        => _client.DeleteAsync(Url(path), TestContext.Current.CancellationToken);

    private async Task<string> CreateAda()
    {
        var response = await Post("/scim/v2/Users", """
            { "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
              "userName": "ada@example.com", "externalId": "8f14e45f", "active": true }
            """);
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    // ---- the media type ----

    [Fact]
    public async Task A_Body_Sent_As_Scim_Json_Is_Accepted()
    {
        // The bug this exists for: ASP.NET's JSON binding does not recognise
        // application/scim+json and answers 415, so the sync fails before a
        // single user is created — and every connector sends exactly that.
        var response = await Post("/scim/v2/Users", """{ "userName": "ada@example.com" }""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Responses_Carry_The_Media_Type_Too()
    {
        var response = await Get("/scim/v2/Users");

        Assert.Equal(BowireScimEndpoints.ContentType, response.Content.Headers.ContentType?.MediaType);
    }

    // ---- authentication ----

    [Fact]
    public async Task Without_A_Token_Nothing_Is_Served()
    {
        using var anonymous = _app.GetTestServer().CreateClient();

        var response = await anonymous.GetAsync(
            Url("/scim/v2/Users"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_Wrong_Token_Is_Refused_With_A_Scim_Error_Body()
    {
        // The connector logs whatever comes back. A bare 401 with an empty
        // body is an operator ticket that starts with "it just says 401".
        using var wrong = _app.GetTestServer().CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "nope");

        var response = await wrong.GetAsync(
            Url("/scim/v2/Users"), TestContext.Current.CancellationToken);
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("401", body.GetProperty("status").GetString());
        Assert.Equal("invalidCredentials", body.GetProperty("scimType").GetString());
    }

    [Fact]
    public async Task Discovery_Is_Behind_The_Token_As_Well()
    {
        // ServiceProviderConfig names the authentication scheme and the
        // limits — an inventory of the surface, not a public document.
        using var anonymous = _app.GetTestServer().CreateClient();

        var response = await anonymous.GetAsync(
            Url("/scim/v2/ServiceProviderConfig"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- discovery ----

    [Fact]
    public async Task ServiceProviderConfig_Says_What_Is_And_Is_Not_Supported()
    {
        // A connector told that bulk works and then given a 404 retries the
        // whole sync rather than falling back.
        var body = await Json(await Get("/scim/v2/ServiceProviderConfig"));

        Assert.True(body.GetProperty("patch").GetProperty("supported").GetBoolean());
        Assert.True(body.GetProperty("filter").GetProperty("supported").GetBoolean());
        Assert.False(body.GetProperty("bulk").GetProperty("supported").GetBoolean());
        Assert.False(body.GetProperty("sort").GetProperty("supported").GetBoolean());
    }

    [Fact]
    public async Task ResourceTypes_Points_At_The_Endpoints_That_Exist()
    {
        var body = await Json(await Get("/scim/v2/ResourceTypes"));
        var endpoints = body.GetProperty("Resources").EnumerateArray()
            .Select(r => r.GetProperty("endpoint").GetString()).ToList();

        Assert.Contains("/scim/v2/Users", endpoints);
        Assert.Contains("/scim/v2/Groups", endpoints);
    }

    [Fact]
    public async Task Schemas_Describes_Both_Resources()
    {
        var body = await Json(await Get("/scim/v2/Schemas"));
        var ids = body.GetProperty("Resources").EnumerateArray()
            .Select(r => r.GetProperty("id").GetString()).ToList();

        Assert.Contains(ScimSchemas.User, ids);
        Assert.Contains(ScimSchemas.Group, ids);
    }

    // ---- the round trip ----

    [Fact]
    public async Task Creating_Answers_201_With_The_Resource_And_Its_Location()
    {
        var response = await Post("/scim/v2/Users", """
            { "userName": "ada@example.com", "externalId": "8f14e45f" }
            """);
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("ada@example.com", body.GetProperty("userName").GetString());
        Assert.True(body.GetProperty("active").GetBoolean());
        Assert.Contains("/scim/v2/Users/",
            body.GetProperty("meta").GetProperty("location").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_The_Same_Login_Name_Twice_Is_A_409_With_Uniqueness()
    {
        // The connector reads scimType to tell "already there" from "broken",
        // and treats the first as success.
        await CreateAda();

        var response = await Post("/scim/v2/Users", """{ "userName": "ada@example.com" }""");
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("uniqueness", body.GetProperty("scimType").GetString());
    }

    [Fact]
    public async Task A_Provisioned_User_Can_Be_Fetched_Back()
    {
        var id = await CreateAda();

        var body = await Json(await Get($"/scim/v2/Users/{id}"));

        Assert.Equal(id, body.GetProperty("id").GetString());
    }

    [Fact]
    public async Task An_Unknown_Id_Is_A_404_Not_An_Empty_Resource()
    {
        var response = await Get("/scim/v2/Users/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_Filter_Every_Connector_Sends_Finds_The_One_User()
    {
        await CreateAda();
        await Post("/scim/v2/Users", """{ "userName": "grace@example.com" }""");

        var body = await Json(await Get(
            "/scim/v2/Users?filter=userName%20eq%20%22ada%40example.com%22"));

        Assert.Equal(1, body.GetProperty("totalResults").GetInt32());
        Assert.Equal("ada@example.com",
            body.GetProperty("Resources")[0].GetProperty("userName").GetString());
    }

    [Fact]
    public async Task A_Filter_Outside_The_Subset_Is_A_400_With_InvalidFilter()
    {
        var response = await Get("/scim/v2/Users?filter=userName%20co%20%22example%22");
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalidFilter", body.GetProperty("scimType").GetString());
    }

    [Fact]
    public async Task Paging_Starts_At_One_Not_Zero()
    {
        // RFC 7644 §3.4.2.4. A connector asking for startIndex=1 and getting
        // the second resource silently skips the first user in the directory.
        await CreateAda();
        await Post("/scim/v2/Users", """{ "userName": "grace@example.com" }""");

        var body = await Json(await Get("/scim/v2/Users?startIndex=1&count=1"));

        Assert.Equal(2, body.GetProperty("totalResults").GetInt32());
        Assert.Equal(1, body.GetProperty("startIndex").GetInt32());
        Assert.Equal(1, body.GetProperty("itemsPerPage").GetInt32());
        Assert.Equal("ada@example.com",
            body.GetProperty("Resources")[0].GetProperty("userName").GetString());
    }

    [Fact]
    public async Task The_Second_Page_Continues_Where_The_First_Stopped()
    {
        await CreateAda();
        await Post("/scim/v2/Users", """{ "userName": "grace@example.com" }""");

        var body = await Json(await Get("/scim/v2/Users?startIndex=2&count=1"));

        Assert.Equal("grace@example.com",
            body.GetProperty("Resources")[0].GetProperty("userName").GetString());
    }

    // ---- deprovisioning over the wire ----

    [Fact]
    public async Task Entras_Pathless_Patch_Deactivates()
    {
        var id = await CreateAda();

        var response = await Patch($"/scim/v2/Users/{id}", """
            { "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
              "Operations": [ { "op": "Replace", "value": { "active": false } } ] }
            """);
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Oktas_Patch_Deactivates_Too()
    {
        var id = await CreateAda();

        var response = await Patch($"/scim/v2/Users/{id}", """
            { "Operations": [ { "op": "replace", "path": "active", "value": false } ] }
            """);

        Assert.False((await Json(response)).GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Delete_Answers_204_And_Leaves_A_Deactivated_Record()
    {
        // A soft delete, so the record is still fetchable — which is also what
        // lets a connector reactivate instead of re-creating.
        var id = await CreateAda();

        var response = await Delete($"/scim/v2/Users/{id}");
        var after = await Json(await Get($"/scim/v2/Users/{id}"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(after.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Replacing_A_User_Keeps_Its_Id()
    {
        var id = await CreateAda();

        var response = await Put($"/scim/v2/Users/{id}", """
            { "userName": "ada@example.com", "displayName": "Ada L." }
            """);
        var body = await Json(response);

        Assert.Equal(id, body.GetProperty("id").GetString());
        Assert.Equal("Ada L.", body.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task A_Malformed_Body_Is_A_400_Rather_Than_A_500()
    {
        var response = await Post("/scim/v2/Users", "{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalidSyntax", (await Json(response)).GetProperty("scimType").GetString());
    }

    // ---- groups ----

    [Fact]
    public async Task A_Group_Round_Trips_With_Its_Members()
    {
        var ada = await CreateAda();

        var created = await Post("/scim/v2/Groups", $$"""
            { "schemas": ["urn:ietf:params:scim:schemas:core:2.0:Group"],
              "displayName": "bowire-admins",
              "members": [ { "value": "{{ada}}" } ] }
            """);
        var body = await Json(created);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("bowire-admins", body.GetProperty("displayName").GetString());
        Assert.Equal(ada, body.GetProperty("members")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Group_Membership_Is_Patched_The_Way_Connectors_Sync_It()
    {
        var ada = await CreateAda();
        var group = (await Json(await Post("/scim/v2/Groups",
            """{ "displayName": "bowire-admins" }"""))).GetProperty("id").GetString();

        var response = await Patch($"/scim/v2/Groups/{group}", $$"""
            { "Operations": [
                { "op": "add", "path": "members", "value": [ { "value": "{{ada}}" } ] } ] }
            """);

        Assert.Equal(ada, (await Json(response)).GetProperty("members")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_Group_Can_Be_Removed_Outright()
    {
        var group = (await Json(await Post("/scim/v2/Groups",
            """{ "displayName": "bowire-admins" }"""))).GetProperty("id").GetString();

        var response = await Delete($"/scim/v2/Groups/{group}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
