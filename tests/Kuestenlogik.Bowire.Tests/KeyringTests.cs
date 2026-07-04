// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Keyring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Coverage for the optional <c>Kuestenlogik.Bowire.Keyring</c> package
/// (#208 Phase 5): reference parsing, the <see cref="KeyringOptions.Enabled"/>
/// gate, the OS backend's platform selection, and the batch endpoint's
/// value / error / disabled shaping (driven through a fake backend so no
/// real credential store is touched).
/// </summary>
public sealed class KeyringTests
{
    // ---- KeyringReference.TryParse ------------------------------------

    [Theory]
    [InlineData("github.com/deploy-bot", "github.com", "deploy-bot")]
    [InlineData("service-only", "service-only", null)]
    [InlineData("  spaced/acct  ", "spaced", "acct")]
    [InlineData("https://api.example.com/bot", "https:", "/api.example.com/bot")]
    [InlineData("svc/", "svc", null)]
    public void TryParse_SplitsOnFirstSlash(string input, string service, string? account)
    {
        Assert.True(KeyringReference.TryParse(input, out var parsed));
        Assert.Equal(service, parsed.Service);
        Assert.Equal(account, parsed.Account);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/only-account")]
    public void TryParse_RejectsEmptyOrServicelessRefs(string input)
    {
        Assert.False(KeyringReference.TryParse(input, out _));
    }

    // ---- KeyringResolver gate + status mapping ------------------------

    [Fact]
    public void Resolve_WhenDisabled_ReturnsFailedWithoutTouchingBackend()
    {
        var backend = new FakeKeyringBackend();
        backend.Store["svc/acct"] = "s3cret";
        var resolver = new KeyringResolver(new KeyringOptions { Enabled = false }, backend);

        var result = resolver.Resolve("svc/acct");

        Assert.Equal(KeyringReadStatus.Error, result.Status);
        Assert.Equal(0, backend.ReadCount); // gate short-circuits before the read
    }

    [Fact]
    public void Resolve_Found_ReturnsValue()
    {
        var backend = new FakeKeyringBackend();
        backend.Store["github.com/deploy-bot"] = "ghp_xxx";
        var resolver = new KeyringResolver(new KeyringOptions { Enabled = true }, backend);

        var result = resolver.Resolve("github.com/deploy-bot");

        Assert.Equal(KeyringReadStatus.Found, result.Status);
        Assert.Equal("ghp_xxx", result.Value);
    }

    [Fact]
    public void Resolve_Miss_ReturnsNotFound()
    {
        var resolver = new KeyringResolver(new KeyringOptions { Enabled = true }, new FakeKeyringBackend());

        var result = resolver.Resolve("absent/entry");

        Assert.Equal(KeyringReadStatus.NotFound, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Resolve_InvalidReference_ReturnsError()
    {
        var resolver = new KeyringResolver(new KeyringOptions { Enabled = true }, new FakeKeyringBackend());

        var result = resolver.Resolve("   ");

        Assert.Equal(KeyringReadStatus.Error, result.Status);
    }

    // ---- OsKeyringBackend platform selection --------------------------

    [Theory]
    [InlineData("wincred")]
    [InlineData("keychain")]
    [InlineData("secret-tool")]
    [InlineData("none")]
    public void OsBackend_HonoursExplicitOverride(string forced)
    {
        var backend = new OsKeyringBackend(forced);
        Assert.Equal(forced, backend.BackendId);
    }

    [Fact]
    public void OsBackend_ForcedNone_ReadsFail()
    {
        var backend = new OsKeyringBackend("none");
        var result = backend.Read(new KeyringReference("svc", null));
        Assert.Equal(KeyringReadStatus.Error, result.Status);
    }

    [Fact]
    public void OsBackend_AutoSelectsForCurrentOs()
    {
        var backend = new OsKeyringBackend("auto");
        var expected =
            OperatingSystem.IsWindows() ? "wincred" :
            OperatingSystem.IsMacOS() ? "keychain" :
            OperatingSystem.IsLinux() ? "secret-tool" : "none";
        Assert.Equal(expected, backend.BackendId);
    }

    // ---- Batch endpoint -----------------------------------------------

    private static readonly string[] FoundAndMissingRefs = ["svc/found", "svc/missing"];
    private static readonly string[] FoundRef = ["svc/found"];
    private static readonly Uri StatusUri = new("/api/vars/keyring/status", UriKind.Relative);

    [Fact]
    public async Task Endpoint_ResolvesHits_AndReportsMisses()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeKeyringBackend();
        backend.Store["svc/found"] = "value-1";
        await using var app = new KeyringTestApp(new KeyringOptions { Enabled = true }, backend);
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/vars/keyring",
            new { refs = FoundAndMissingRefs }, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("value-1", root.GetProperty("values").GetProperty("svc/found").GetString());
        Assert.False(root.GetProperty("values").TryGetProperty("svc/missing", out _));
        Assert.Equal("not found", root.GetProperty("errors").GetProperty("svc/missing").GetString());
    }

    [Fact]
    public async Task Endpoint_WhenDisabled_ReturnsEmptyEnabledFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeKeyringBackend();
        backend.Store["svc/found"] = "value-1";
        await using var app = new KeyringTestApp(new KeyringOptions { Enabled = false }, backend);
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/vars/keyring",
            new { refs = FoundRef }, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Empty(doc.RootElement.GetProperty("values").EnumerateObject());
        Assert.Equal(0, backend.ReadCount); // never touched the store
    }

    [Fact]
    public async Task Status_ReportsEnabledAndBackend()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = new KeyringTestApp(
            new KeyringOptions { Enabled = true }, new FakeKeyringBackend("secret-tool"));
        using var client = app.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync(StatusUri, ct));

        Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("secret-tool", doc.RootElement.GetProperty("backend").GetString());
    }

    // ---- Test doubles --------------------------------------------------

    private sealed class FakeKeyringBackend(string backendId = "fake") : IKeyringBackend
    {
        public Dictionary<string, string> Store { get; } = new(StringComparer.Ordinal);
        public int ReadCount { get; private set; }
        public string BackendId { get; } = backendId;

        public KeyringReadResult Read(KeyringReference reference)
        {
            ReadCount++;
            var key = reference.Account is null
                ? reference.Service
                : $"{reference.Service}/{reference.Account}";
            return Store.TryGetValue(key, out var v)
                ? KeyringReadResult.Found(v)
                : KeyringReadResult.NotFound();
        }
    }

    /// <summary>
    /// Kestrel bound to an ephemeral loopback port with just the keyring
    /// endpoints mapped — mirrors the pattern the proxy / auth endpoint
    /// tests use so we don't pull in TestHost.
    /// </summary>
    private sealed class KeyringTestApp : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _baseUrl;

        public KeyringTestApp(KeyringOptions options, IKeyringBackend backend)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
                o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
            builder.Services.AddSingleton(new KeyringResolver(options, backend));

            _app = builder.Build();
            _app.MapBowireKeyringEndpoints(basePath: string.Empty);
            _app.StartAsync().GetAwaiter().GetResult();
            _baseUrl = _app.Urls.First();
        }

        public HttpClient CreateClient() => new() { BaseAddress = new Uri(_baseUrl) };

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
