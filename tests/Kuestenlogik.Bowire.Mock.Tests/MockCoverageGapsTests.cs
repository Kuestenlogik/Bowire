// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Mock.Management;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Targeted tests for the Mock plugin's small remaining gaps:
/// the internal <c>NullMockLogger</c> (0% baseline) which is the
/// fallback when no <see cref="ILoggerFactory"/> is registered, and
/// the <see cref="BowireMockManagementEndpoints"/> error branches
/// (malformed JSON, missing recording, 404 paths) that don't fire
/// in the happy-path lifecycle tests.
/// </summary>
public sealed class MockCoverageGapsTests
{
    // ---- NullMockLogger -------------------------------------------

    [Fact]
    public void NullMockLogger_Singleton_IsReused()
    {
        // The Instance field is what ResolveLogger hands back when no
        // factory is registered — same reference each lookup, never
        // null. Pinning this guarantees the singleton invariant.
        var t = typeof(BowireMockApplicationBuilderExtensions).Assembly
            .GetType("Kuestenlogik.Bowire.Mock.NullMockLogger")!;
        var instance = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null);
        var again = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null);
        Assert.NotNull(instance);
        Assert.Same(instance, again);
        Assert.IsAssignableFrom<ILogger>(instance);
    }

    [Theory]
    [InlineData(LogLevel.Critical)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.None)]
    public void NullMockLogger_IsEnabled_AlwaysFalse(LogLevel level)
    {
        // The whole point of NullMockLogger is that the mock handler
        // can skip the formatter when no logger is registered. Pinning
        // IsEnabled=false stops a refactor from accidentally enabling
        // it for some level.
        var t = typeof(BowireMockApplicationBuilderExtensions).Assembly
            .GetType("Kuestenlogik.Bowire.Mock.NullMockLogger")!;
        var logger = (ILogger)t.GetField("Instance", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        Assert.False(logger.IsEnabled(level));
    }

    [Fact]
    public void NullMockLogger_BeginScope_ReturnsNull()
    {
        var t = typeof(BowireMockApplicationBuilderExtensions).Assembly
            .GetType("Kuestenlogik.Bowire.Mock.NullMockLogger")!;
        var logger = (ILogger)t.GetField("Instance", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        var scope = logger.BeginScope("anything");
        Assert.Null(scope);
    }

    [Fact]
    public void NullMockLogger_Log_DoesNotInvokeFormatter()
    {
        // No-op Log → the formatter should never be called, so flipping
        // a flag inside the delegate confirms the implementation skips
        // formatting entirely.
        var t = typeof(BowireMockApplicationBuilderExtensions).Assembly
            .GetType("Kuestenlogik.Bowire.Mock.NullMockLogger")!;
        var logger = (ILogger)t.GetField("Instance", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;

        var formatterCalled = false;
        Func<string, Exception?, string> formatter = (_, _) =>
        {
            formatterCalled = true;
            return "should not see this";
        };
        logger.Log(LogLevel.Error, new EventId(0), "state", new InvalidOperationException("x"), formatter);
        Assert.False(formatterCalled);
    }

    // ---- BowireMockManagementEndpoints error branches -------------

    [Fact]
    public async Task GetMocks_When_Registry_Empty_ReturnsEmptyArray()
    {
        await using var host = await StartHostAsync();
        var json = await host.Client.GetStringAsync(new Uri("/bowire/api/mocks", UriKind.Relative),
            TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("mocks", out var arr));
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(0, arr.GetArrayLength());
    }

    [Fact]
    public async Task GetMock_NotFound_Returns404_WithErrorBody()
    {
        await using var host = await StartHostAsync();
        using var resp = await host.Client.GetAsync(
            new Uri("/bowire/api/mocks/does-not-exist", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Contains("does-not-exist", doc.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteMock_NotFound_Returns404_WithErrorBody()
    {
        await using var host = await StartHostAsync();
        using var resp = await host.Client.DeleteAsync(
            new Uri("/bowire/api/mocks/none-here", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("none-here", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMockRequests_NotFound_Returns404()
    {
        await using var host = await StartHostAsync();
        using var resp = await host.Client.GetAsync(
            new Uri("/bowire/api/mocks/no-such-mock/requests", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PostMock_InvalidJson_Returns400_WithParseError()
    {
        await using var host = await StartHostAsync();
        using var content = new StringContent("{not valid json", Encoding.UTF8, "application/json");
        using var resp = await host.Client.PostAsync(
            new Uri("/bowire/api/mocks", UriKind.Relative), content,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Contains("Invalid JSON", doc.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostMock_EmptyBody_Returns400()
    {
        // Empty body parses as a JsonException ("The input does not
        // contain any JSON tokens"), so the 400 carries the
        // "Invalid JSON" parser message — not the "recording required"
        // message that follows the parse stage.
        await using var host = await StartHostAsync();
        using var content = new StringContent("", Encoding.UTF8, "application/json");
        using var resp = await host.Client.PostAsync(
            new Uri("/bowire/api/mocks", UriKind.Relative), content,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Invalid JSON", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostMock_JsonNullBody_Returns400_WithRequiredMessage()
    {
        // `null` deserializes to a null StartMockRequest reference →
        // the post-parse `req is null` guard fires with the
        // "recording required" 400.
        await using var host = await StartHostAsync();
        using var content = new StringContent("null", Encoding.UTF8, "application/json");
        using var resp = await host.Client.PostAsync(
            new Uri("/bowire/api/mocks", UriKind.Relative), content,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("required", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostMock_MissingRecordingField_Returns400()
    {
        await using var host = await StartHostAsync();
        using var content = new StringContent(
            """{"name":"x","port":0}""", Encoding.UTF8, "application/json");
        using var resp = await host.Client.PostAsync(
            new Uri("/bowire/api/mocks", UriKind.Relative), content,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // Body must carry either `recording` (inline JSON) or `recordingId` —
        // assert the error mentions one of them so a future copy-edit
        // doesn't silently break the contract.
        Assert.Contains("recording", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostMock_WhitespaceOnlyRecording_Returns400()
    {
        // The endpoint trims-checks Recording so blank-strings get
        // rejected at the parser layer before any disk I/O.
        await using var host = await StartHostAsync();
        using var content = new StringContent(
            """{"recording":"   "}""", Encoding.UTF8, "application/json");
        using var resp = await host.Client.PostAsync(
            new Uri("/bowire/api/mocks", UriKind.Relative), content,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostMock_RegistryStartFails_Returns500()
    {
        // Hand the registry a payload that fails MockServer's parse
        // step — e.g. plainly not a JSON recording.
        await using var host = await StartHostAsync();
        using var content = new StringContent(
            """{"recording":"not a recording document","name":"bogus"}""",
            Encoding.UTF8, "application/json");
        using var resp = await host.Client.PostAsync(
            new Uri("/bowire/api/mocks", UriKind.Relative), content,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.False(string.IsNullOrWhiteSpace(err.GetString()));
    }

    // ---- helpers ---------------------------------------------------

    private sealed record TestHostBundle(IHost Host, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.StopAsync();
            Host.Dispose();
        }
    }

    private static async Task<TestHostBundle> StartHostAsync()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddSingleton<BowireMockHostManager>();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapBowireMockManagement());
                });
            });
        var host = await builder.StartAsync();
        var client = host.GetTestClient();
        client.BaseAddress = new Uri("http://localhost");
        return new TestHostBundle(host, client);
    }
}
