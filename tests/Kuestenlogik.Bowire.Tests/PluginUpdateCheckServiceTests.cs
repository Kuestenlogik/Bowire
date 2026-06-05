// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for <see cref="PluginUpdateCheckService"/> — the opt-in nuget.org
/// poll that powers the daily background update check + the manual
/// "check now" button in Settings. Swap <see cref="PluginUpdateCheckService.PluginDir"/>
/// and <see cref="BowireUserContext.Current"/> to temp paths so the
/// scan + cache write don't touch the developer's real <c>~/.bowire/</c>.
/// </summary>
public sealed class PluginUpdateCheckServiceTests : IDisposable
{
    private readonly string _originalPluginDir;
    private readonly IBowireUserStore _originalUserStore;
    private readonly string _pluginDir;
    private readonly string _userStoreRoot;

    public PluginUpdateCheckServiceTests()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"bowire-pluginupd-{Guid.NewGuid():N}");
        _pluginDir = Path.Combine(sandbox, "plugins");
        _userStoreRoot = Path.Combine(sandbox, "userstore");
        Directory.CreateDirectory(_pluginDir);
        Directory.CreateDirectory(_userStoreRoot);

        _originalPluginDir = PluginUpdateCheckService.PluginDir;
        _originalUserStore = BowireUserContext.Current;
        PluginUpdateCheckService.PluginDir = _pluginDir;
        BowireUserContext.Current = new TempStore(_userStoreRoot);
    }

    public void Dispose()
    {
        PluginUpdateCheckService.PluginDir = _originalPluginDir;
        BowireUserContext.Current = _originalUserStore;
        var sandbox = Path.GetDirectoryName(_pluginDir);
        if (sandbox is not null && Directory.Exists(sandbox))
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { /* best-effort */ }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task CheckAsync_With_No_Plugins_Installed_Returns_Empty_Results_And_Writes_Cache()
    {
        // PluginDir exists but is empty — the scan returns zero rows.
        // The snapshot still persists so ReadCached can replay it.
        using var http = NewHttpClient(_ => throw new InvalidOperationException("no HTTP calls expected"));
        var service = new PluginUpdateCheckService(http);

        var snapshot = await service.CheckAsync(includePrerelease: false, TestContext.Current.CancellationToken);

        Assert.Empty(snapshot.Results);
        Assert.False(snapshot.IncludePrerelease);

        var cached = PluginUpdateCheckService.ReadCached();
        Assert.NotNull(cached);
        Assert.Empty(cached!.Results);
    }

    [Fact]
    public async Task CheckAsync_Picks_Latest_Stable_For_Each_Installed_Plugin()
    {
        await WritePluginManifestAsync("Kuestenlogik.Bowire.Protocol.Demo", "1.0.0");
        await WritePluginManifestAsync("Kuestenlogik.Bowire.Protocol.Other", "2.0.0");

        using var http = NewHttpClient(url =>
        {
            if (url.Contains("kuestenlogik.bowire.protocol.demo", StringComparison.Ordinal))
                return Json("""{"versions":["1.0.0","1.1.0","1.2.0","1.3.0-preview"]}""");
            if (url.Contains("kuestenlogik.bowire.protocol.other", StringComparison.Ordinal))
                return Json("""{"versions":["2.0.0","2.1.0"]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = new PluginUpdateCheckService(http);

        var snapshot = await service.CheckAsync(includePrerelease: false, TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.Results.Count);
        var demo = snapshot.Results.Single(r => r.PackageId == "Kuestenlogik.Bowire.Protocol.Demo");
        Assert.Equal("1.2.0", demo.Latest);
        Assert.True(demo.UpdateAvailable);
        var other = snapshot.Results.Single(r => r.PackageId == "Kuestenlogik.Bowire.Protocol.Other");
        Assert.Equal("2.1.0", other.Latest);
        Assert.True(other.UpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_Reports_NoUpdate_When_Installed_Matches_Latest()
    {
        await WritePluginManifestAsync("Kuestenlogik.Bowire.Protocol.Demo", "1.2.0");

        using var http = NewHttpClient(_ =>
            Json("""{"versions":["1.0.0","1.1.0","1.2.0"]}"""));
        var service = new PluginUpdateCheckService(http);

        var snapshot = await service.CheckAsync(includePrerelease: false, TestContext.Current.CancellationToken);
        var row = Assert.Single(snapshot.Results);
        Assert.Equal("1.2.0", row.Latest);
        Assert.False(row.UpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_Includes_Prerelease_When_Flag_Set()
    {
        await WritePluginManifestAsync("Kuestenlogik.Bowire.Protocol.Demo", "1.0.0");

        using var http = NewHttpClient(_ =>
            Json("""{"versions":["1.0.0","1.1.0","1.2.0-preview","1.3.0-rc.1"]}"""));
        var service = new PluginUpdateCheckService(http);

        var snapshot = await service.CheckAsync(includePrerelease: true, TestContext.Current.CancellationToken);
        var row = Assert.Single(snapshot.Results);
        Assert.Equal("1.3.0-rc.1", row.Latest);
        Assert.True(snapshot.IncludePrerelease);
    }

    [Fact]
    public async Task CheckAsync_Captures_Per_Package_Errors_Without_Failing_The_Snapshot()
    {
        // The whole snapshot should survive a 5xx on one of the packages;
        // the error lands on the row so the UI can highlight that
        // specific plugin without blanking out the others.
        await WritePluginManifestAsync("Kuestenlogik.Bowire.Protocol.Good", "1.0.0");
        await WritePluginManifestAsync("Kuestenlogik.Bowire.Protocol.Bad", "1.0.0");

        using var http = NewHttpClient(url =>
        {
            if (url.Contains("good", StringComparison.Ordinal))
                return Json("""{"versions":["1.0.0","1.1.0"]}""");
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var service = new PluginUpdateCheckService(http);

        var snapshot = await service.CheckAsync(includePrerelease: false, TestContext.Current.CancellationToken);

        var good = snapshot.Results.Single(r => r.PackageId == "Kuestenlogik.Bowire.Protocol.Good");
        Assert.Null(good.Error);
        Assert.Equal("1.1.0", good.Latest);

        var bad = snapshot.Results.Single(r => r.PackageId == "Kuestenlogik.Bowire.Protocol.Bad");
        Assert.NotNull(bad.Error);
        Assert.Null(bad.Latest);
        Assert.False(bad.UpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_Skips_Plugin_Directory_Without_Manifest()
    {
        // A directory under plugins/ without a plugin.json is ignored
        // rather than crashing the scan (e.g. half-written install
        // mid-extraction).
        Directory.CreateDirectory(Path.Combine(_pluginDir, "Kuestenlogik.Bowire.Protocol.Demo"));

        using var http = NewHttpClient(_ => throw new InvalidOperationException("no HTTP expected"));
        var service = new PluginUpdateCheckService(http);

        var snapshot = await service.CheckAsync(includePrerelease: false, TestContext.Current.CancellationToken);
        Assert.Empty(snapshot.Results);
    }

    [Fact]
    public async Task CheckAsync_Skips_Plugin_With_Corrupted_Manifest()
    {
        var dir = Path.Combine(_pluginDir, "Kuestenlogik.Bowire.Protocol.Demo");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "plugin.json"), "{not valid json", TestContext.Current.CancellationToken);

        using var http = NewHttpClient(_ => throw new InvalidOperationException("no HTTP expected"));
        var service = new PluginUpdateCheckService(http);

        var snapshot = await service.CheckAsync(includePrerelease: false, TestContext.Current.CancellationToken);
        Assert.Empty(snapshot.Results);
    }

    [Fact]
    public void ReadCached_Returns_Null_When_Cache_File_Missing()
    {
        Assert.Null(PluginUpdateCheckService.ReadCached());
    }

    [Fact]
    public void ReadCached_Returns_Null_When_Cache_File_Corrupted()
    {
        var dir = BowireUserContext.GetUserPath("state");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "update-check.json"), "{not json");

        Assert.Null(PluginUpdateCheckService.ReadCached());
    }

    [Fact]
    public async Task ReadCached_Returns_Persisted_Snapshot_After_Check()
    {
        await WritePluginManifestAsync("Kuestenlogik.Bowire.Protocol.Demo", "1.0.0");
        using var http = NewHttpClient(_ =>
            Json("""{"versions":["1.0.0","1.1.0"]}"""));
        var service = new PluginUpdateCheckService(http);

        await service.CheckAsync(includePrerelease: false, TestContext.Current.CancellationToken);
        var cached = PluginUpdateCheckService.ReadCached();

        Assert.NotNull(cached);
        Assert.Single(cached!.Results);
        Assert.Equal("Kuestenlogik.Bowire.Protocol.Demo", cached.Results[0].PackageId);
    }

    private async Task WritePluginManifestAsync(string packageId, string version)
    {
        var dir = Path.Combine(_pluginDir, packageId);
        Directory.CreateDirectory(dir);
        var manifest = JsonSerializer.Serialize(new { packageId, version });
        await File.WriteAllTextAsync(Path.Combine(dir, "plugin.json"), manifest, TestContext.Current.CancellationToken);
    }

    private static HttpClient NewHttpClient(Func<string, HttpResponseMessage> handler)
    {
        // CA2000: HttpClient owns the handler when constructed with the
        // single-arg ctor and disposes it on HttpClient.Dispose(); the
        // analyzer can't see the ownership transfer.
#pragma warning disable CA2000
        return new HttpClient(new FakeHttpMessageHandler(handler));
#pragma warning restore CA2000
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    private sealed class FakeHttpMessageHandler(Func<string, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            return Task.FromResult(handler(url));
        }
    }

    private sealed class TempStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }
}
