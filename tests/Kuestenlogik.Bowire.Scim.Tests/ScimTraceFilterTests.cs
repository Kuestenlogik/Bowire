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
/// The provisioning trace as the real endpoints write it (#816): the status a
/// line records is the one the connector received.
/// </summary>
/// <remarks>
/// An endpoint filter sees the handler's result before it is executed, so a
/// status read there is still the default 200 — for a refused request, for a
/// created one, for one that was not found. The 4xx lines are the ones #639
/// needs most.
/// </remarks>
public sealed class ScimTraceFilterTests : IAsyncLifetime
{
    private const string Token = "s3cr3t-provisioning-token";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-scim-trace-" + Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new BowireScimOptions { Enabled = true, Token = Token, TraceProvisioning = true });
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

    private List<JsonElement> Lines() =>
        [.. File.ReadAllLines(Path.Combine(_root, BowireScimStore.DirectoryName, "trace.jsonl"))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())];

    [Fact]
    public async Task Each_Line_Records_The_Status_The_Connector_Received()
    {
        var ct = TestContext.Current.CancellationToken;

        using var anonymous = _app.GetTestServer().CreateClient();
        using var refused = await anonymous.GetAsync(new Uri("/scim/v2/ServiceProviderConfig", UriKind.Relative), ct);
        using var user = new StringContent(
            """{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"ada@example.com","active":true}""",
            Encoding.UTF8, BowireScimEndpoints.ContentType);
        using var created = await _client.PostAsync(new Uri("/scim/v2/Users", UriKind.Relative), user, ct);
        using var missing = await _client.GetAsync(new Uri("/scim/v2/Users/nobody", UriKind.Relative), ct);
        using var read = await _client.GetAsync(new Uri("/scim/v2/Users", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        Assert.Equal([401, 201, 404, 200], Lines().Select(l => l.GetProperty("status").GetInt32()));
    }

    [Fact]
    public async Task A_Patch_Body_That_Is_Not_An_Object_Is_Still_A_400_And_A_Line()
    {
        // #876 — the dialect detector threw on a JSON array, which turned a
        // malformed PATCH into a 500 and left no line, on the one surface
        // whose job is to show what a connector sent.
        var ct = TestContext.Current.CancellationToken;
        using var body = new StringContent("[]", Encoding.UTF8, BowireScimEndpoints.ContentType);

        using var response = await _client.PatchAsync(new Uri("/scim/v2/Users/nobody", UriKind.Relative), body, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, Assert.Single(Lines()).GetProperty("status").GetInt32());
    }
}
