// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Mcp;
using Kuestenlogik.Bowire.Mock;
using Kuestenlogik.Bowire.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Serialises tests that flip <see cref="BowireMcpTools.HomeDirOverride"/>
/// — the override is process-global, so concurrent xUnit workers would
/// race. xUnit requires the collection-definition class to be public.
/// </summary>
[CollectionDefinition(nameof(BowireConfigFixture))]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit collection definition must be public.")]
public sealed class BowireConfigFixture { }

/// <summary>
/// Additional unit tests for <see cref="BowireMcpTools"/>,
/// <see cref="BowireMockHandleRegistry"/>, and the seed-from-environments
/// path that aren't reached by <see cref="BowireMcpToolsTests"/>. The split
/// keeps the original file's StubProtocol fixture tightly scoped while
/// these tests focus on file-system, error-branch, and lifecycle paths.
///
/// <para>
/// Tests that touch <c>environments.json</c> / <c>recordings.json</c>
/// flip the test-only <see cref="BowireMcpTools.HomeDirOverride"/> so
/// the lookups land in a per-test temp dir instead of the real
/// <c>~/.bowire/</c>. <see cref="BowireConfigFixture"/> serialises them
/// because the override is a process-wide static. This avoids ever
/// touching the developer's actual config files (a previous version of
/// these tests backed them up in-place — risky if the run was killed
/// mid-test).
/// </para>
/// </summary>
[Collection(nameof(BowireConfigFixture))]
public sealed class BowireMcpCoverageTests : IAsyncDisposable
{
    private readonly List<BowireMockHandleRegistry> _registries = [];
    private readonly List<string> _tempDirs = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _registries)
        {
            await r.DisposeAsync();
        }
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
        GC.SuppressFinalize(this);
    }

    private string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bowire-mcp-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private BowireMockHandleRegistry NewMockHandles()
    {
        var r = new BowireMockHandleRegistry();
        _registries.Add(r);
        return r;
    }

    private BowireMcpTools BuildTools(
        BowireProtocolRegistry? registry = null,
        BowireMcpOptions? options = null)
    {
        registry ??= new BowireProtocolRegistry();
        options ??= new BowireMcpOptions { LoadAllowlistFromEnvironments = false };
        return new BowireMcpTools(
            registry,
            NewMockHandles(),
            Options.Create(options),
            NullLogger<BowireMcpTools>.Instance);
    }

    // -------------------- Discover error branch --------------------

    [Fact]
    public async Task Discover_Plugin_Throwing_Is_Swallowed_And_Logged()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new ThrowingProtocol("boom", "Boom"));

        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            AllowArbitraryUrls = true,
        };
        var tools = BuildTools(registry: registry, options: options);

        var json = await tools.Discover(
            "http://localhost:9999",
            protocol: "boom",
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(json);
        // The plugin threw; the response is still valid JSON with empty services.
        Assert.Equal("http://localhost:9999", doc.RootElement.GetProperty("url").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("services").GetArrayLength());
    }

    // -------------------- EnvList / RecordList file-present branches --------------------

    [Fact]
    public void EnvList_With_Existing_File_Echoes_Parsed_Document()
    {
        WithSwappedConfigFile("environments.json", /*lang=json,strict*/ """
            [ { "name": "dev", "serverUrl": "http://localhost:5000" } ]
            """, () =>
        {
            var json = BowireMcpTools.EnvList();

            using var doc = JsonDocument.Parse(json);
            var env = doc.RootElement.GetProperty("environments");
            Assert.Equal(JsonValueKind.Array, env.ValueKind);
            Assert.Equal("dev", env[0].GetProperty("name").GetString());
        });
    }

    [Fact]
    public void EnvList_With_Malformed_File_Returns_Failure_Message()
    {
        WithSwappedConfigFile("environments.json", "{not-valid-json", () =>
        {
            var result = BowireMcpTools.EnvList();
            Assert.Contains("Failed to read environments.json", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RecordList_With_Existing_Array_Summarises_Each_Recording()
    {
        WithSwappedConfigFile("recordings.json", /*lang=json,strict*/ """
            [
              { "id": "r1", "name": "first",  "createdAt": "2026-01-01T00:00:00Z", "steps": [{}, {}, {}] },
              { "id": "r2", "name": "second", "createdAt": "2026-01-02T00:00:00Z" },
              { }
            ]
            """, () =>
        {
            var json = BowireMcpTools.RecordList();

            using var doc = JsonDocument.Parse(json);
            var recs = doc.RootElement.GetProperty("recordings");
            Assert.Equal(3, recs.GetArrayLength());

            Assert.Equal("r1", recs[0].GetProperty("id").GetString());
            Assert.Equal("first", recs[0].GetProperty("name").GetString());
            Assert.Equal("2026-01-01T00:00:00Z", recs[0].GetProperty("createdAt").GetString());
            Assert.Equal(3, recs[0].GetProperty("stepCount").GetInt32());

            Assert.Equal("r2", recs[1].GetProperty("id").GetString());
            Assert.Equal(0, recs[1].GetProperty("stepCount").GetInt32());

            // Fully-empty record yields nulls but keeps shape.
            Assert.Equal(JsonValueKind.Null, recs[2].GetProperty("id").ValueKind);
            Assert.Equal(0, recs[2].GetProperty("stepCount").GetInt32());
        });
    }

    [Fact]
    public void RecordList_With_NonArray_Root_Returns_Empty_Summary()
    {
        WithSwappedConfigFile("recordings.json", /*lang=json,strict*/ """{ "not": "an-array" }""", () =>
        {
            var json = BowireMcpTools.RecordList();

            using var doc = JsonDocument.Parse(json);
            Assert.Equal(0, doc.RootElement.GetProperty("recordings").GetArrayLength());
        });
    }

    [Fact]
    public void RecordList_With_Missing_File_Returns_Empty_Recordings()
    {
        WithRemovedConfigFile("recordings.json", () =>
        {
            var json = BowireMcpTools.RecordList();

            using var doc = JsonDocument.Parse(json);
            Assert.Equal(0, doc.RootElement.GetProperty("recordings").GetArrayLength());
        });
    }

    [Fact]
    public void RecordList_With_Malformed_File_Returns_Failure_Message()
    {
        WithSwappedConfigFile("recordings.json", "}{not}{", () =>
        {
            var result = BowireMcpTools.RecordList();
            Assert.Contains("Failed to read recordings.json", result, StringComparison.Ordinal);
        });
    }

    // -------------------- Seed-allowlist-from-environments --------------------

    [Fact]
    public void Ctor_LoadAllowlistFromEnvironments_Adds_ServerUrls_From_File()
    {
        // Mix of nested objects, arrays and a duplicate to exercise the
        // recursive walker + the seen-set dedup.
        WithSwappedConfigFile("environments.json", /*lang=json,strict*/ """
            [
              { "name": "dev",     "serverUrl": "http://localhost:5000" },
              { "name": "staging", "serverUrl": "https://staging.example/v1" },
              { "name": "dup",     "serverUrl": "http://localhost:5000" },
              { "nested": { "serverUrl": "https://nested.example" } }
            ]
            """, () =>
        {
            var options = new BowireMcpOptions { LoadAllowlistFromEnvironments = true };

            var tools = new BowireMcpTools(
                new BowireProtocolRegistry(),
                NewMockHandles(),
                Options.Create(options),
                NullLogger<BowireMcpTools>.Instance);

            // 3 distinct URLs survive the dedup.
            Assert.Contains("http://localhost:5000", options.AllowedServerUrls);
            Assert.Contains("https://staging.example/v1", options.AllowedServerUrls);
            Assert.Contains("https://nested.example", options.AllowedServerUrls);
            Assert.Equal(3, options.AllowedServerUrls.Count);

            using var doc = JsonDocument.Parse(tools.AllowlistShow());
            Assert.Equal(3, doc.RootElement.GetProperty("urls").GetArrayLength());
        });
    }

    [Fact]
    public void Ctor_LoadAllowlistFromEnvironments_Swallows_Malformed_File()
    {
        WithSwappedConfigFile("environments.json", "{not json", () =>
        {
            var options = new BowireMcpOptions { LoadAllowlistFromEnvironments = true };

            // Malformed file lands in the ctor's catch — must not throw.
            _ = new BowireMcpTools(
                new BowireProtocolRegistry(),
                NewMockHandles(),
                Options.Create(options),
                NullLogger<BowireMcpTools>.Instance);

            Assert.Empty(options.AllowedServerUrls);
        });
    }

    [Fact]
    public void Ctor_LoadAllowlistFromEnvironments_Ignores_Missing_File()
    {
        WithRemovedConfigFile("environments.json", () =>
        {
            var options = new BowireMcpOptions { LoadAllowlistFromEnvironments = true };

            _ = new BowireMcpTools(
                new BowireProtocolRegistry(),
                NewMockHandles(),
                Options.Create(options),
                NullLogger<BowireMcpTools>.Instance);

            Assert.Empty(options.AllowedServerUrls);
        });
    }

    [Fact]
    public void Ctor_LoadAllowlistFromEnvironments_Pre_Existing_Urls_Are_Preserved()
    {
        WithSwappedConfigFile("environments.json", /*lang=json,strict*/ """
            [ { "name": "dev", "serverUrl": "http://localhost:5000" } ]
            """, () =>
        {
            var options = new BowireMcpOptions { LoadAllowlistFromEnvironments = true };
            options.AllowedServerUrls.Add("http://manual.example");
            // Same as the file entry — the seen-set dedups it back to one copy.
            options.AllowedServerUrls.Add("http://localhost:5000");

            _ = new BowireMcpTools(
                new BowireProtocolRegistry(),
                NewMockHandles(),
                Options.Create(options),
                NullLogger<BowireMcpTools>.Instance);

            Assert.Contains("http://manual.example", options.AllowedServerUrls);
            Assert.Contains("http://localhost:5000", options.AllowedServerUrls);
            Assert.Equal(2, options.AllowedServerUrls.Count);
        });
    }

    // -------------------- MockStart failure path --------------------

    [Fact]
    public async Task MockStart_With_Missing_Recording_File_Returns_Failure_Message()
    {
        var tools = BuildTools();

        var result = await tools.MockStart(
            recording: Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json"),
            ct: TestContext.Current.CancellationToken);

        Assert.StartsWith("bowire.mock.start failed:", result, StringComparison.Ordinal);
    }

    // -------------------- MockStart -> MockList -> MockStop happy path --------------------

    [Fact]
    public async Task MockStart_Then_List_Then_Stop_Roundtrip()
    {
        var dir = NewTempDir("mock");
        var recordingPath = Path.Combine(dir, "rec.json");
        await File.WriteAllTextAsync(
            recordingPath,
            /*lang=json,strict*/ """
            {
              "id": "rec_ping",
              "name": "ping",
              "recordingFormatVersion": 2,
              "steps": [
                {
                  "id": "step_ping",
                  "protocol": "rest",
                  "service": "Ping",
                  "method": "Ping",
                  "methodType": "Unary",
                  "httpPath": "/ping",
                  "httpVerb": "GET",
                  "status": "OK",
                  "response": "pong"
                }
              ]
            }
            """,
            TestContext.Current.CancellationToken);

        var tools = BuildTools();

        var startJson = await tools.MockStart(
            recording: recordingPath,
            port: 0,
            host: "127.0.0.1",
            ct: TestContext.Current.CancellationToken);

        using (var doc = JsonDocument.Parse(startJson))
        {
            var handle = doc.RootElement.GetProperty("handle").GetString();
            Assert.False(string.IsNullOrEmpty(handle));
            Assert.Equal(12, handle!.Length);
            Assert.True(doc.RootElement.GetProperty("port").GetInt32() > 0);
            Assert.Equal(recordingPath, doc.RootElement.GetProperty("source").GetString());

            // MockList sees the running mock.
            var listJson = tools.MockList();
            using var listDoc = JsonDocument.Parse(listJson);
            Assert.Equal(1, listDoc.RootElement.GetProperty("count").GetInt32());
            var entry = listDoc.RootElement.GetProperty("mocks")[0];
            Assert.Equal(handle, entry.GetProperty("handle").GetString());
            Assert.StartsWith("http://localhost:", entry.GetProperty("url").GetString(), StringComparison.Ordinal);

            // MockStop disposes the running server and removes the handle.
            var stopJson = await tools.MockStop(handle);
            using var stopDoc = JsonDocument.Parse(stopJson);
            Assert.True(stopDoc.RootElement.GetProperty("stopped").GetBoolean());

            // Second stop is a no-op.
            var stopAgain = await tools.MockStop(handle);
            using var stopAgainDoc = JsonDocument.Parse(stopAgain);
            Assert.False(stopAgainDoc.RootElement.GetProperty("stopped").GetBoolean());
        }
    }

    // -------------------- Allowlist denied messaging --------------------

    [Fact]
    public async Task AllowlistDenied_Message_Contains_Url_And_Hint()
    {
        var tools = BuildTools();

        var result = await tools.Discover(
            "https://blocked.example.com/path",
            ct: TestContext.Current.CancellationToken);

        Assert.Contains("https://blocked.example.com/path", result, StringComparison.Ordinal);
        Assert.Contains("--allow-arbitrary-urls", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsUrlAllowed_Empty_Url_Is_Denied_Even_With_AllowedUrls_Set()
    {
        var options = new BowireMcpOptions { LoadAllowlistFromEnvironments = false };
        options.AllowedServerUrls.Add("http://localhost:5000");
        var tools = BuildTools(options: options);

        // Whitespace-only URL trips the IsNullOrWhiteSpace short-circuit.
        var result = await tools.Invoke(
            "   ",
            "Service",
            "Method",
            ct: TestContext.Current.CancellationToken);

        Assert.Contains("not on the allowlist", result, StringComparison.Ordinal);
    }

    // -------------------- Helpers --------------------

    /// <summary>
    /// Point <see cref="BowireMcpTools.HomeDirOverride"/> at a fresh temp
    /// directory containing only <c>.bowire/{filename}</c> with
    /// <paramref name="newContents"/>, run <paramref name="action"/>, then
    /// drop the temp dir + clear the override. Serialised by
    /// <see cref="BowireConfigFixture"/> because the override is process-global.
    /// </summary>
    private static void WithSwappedConfigFile(string filename, string newContents, Action action)
    {
        var previousOverride = BowireMcpTools.HomeDirOverride;
        var tempHome = Path.Combine(Path.GetTempPath(), $"bowire-mcp-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempHome, ".bowire"));
        File.WriteAllText(Path.Combine(tempHome, ".bowire", filename), newContents);
        BowireMcpTools.HomeDirOverride = tempHome;
        try
        {
            action();
        }
        finally
        {
            BowireMcpTools.HomeDirOverride = previousOverride;
            try { Directory.Delete(tempHome, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Same dance, but the per-test home is empty — exercises the
    /// file-missing branch without touching the developer's real
    /// <c>~/.bowire/</c>.
    /// </summary>
    private static void WithRemovedConfigFile(string filename, Action action)
    {
        _ = filename; // kept for call-site readability — the file is absent by construction
        var previousOverride = BowireMcpTools.HomeDirOverride;
        var tempHome = Path.Combine(Path.GetTempPath(), $"bowire-mcp-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempHome, ".bowire"));
        BowireMcpTools.HomeDirOverride = tempHome;
        try
        {
            action();
        }
        finally
        {
            BowireMcpTools.HomeDirOverride = previousOverride;
            try { Directory.Delete(tempHome, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Plugin whose <see cref="DiscoverAsync"/> always throws, used to drive
    /// the catch-and-log branch inside <see cref="BowireMcpTools.Discover"/>.
    /// </summary>
    private sealed class ThrowingProtocol : IBowireProtocol
    {
        public ThrowingProtocol(string id, string name) { Id = id; Name = name; }

        public string Id { get; }
        public string Name { get; }
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => throw new InvalidOperationException("plugin discovery exploded");

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new InvalidOperationException("plugin invoke exploded");

        public async IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield break;
        }

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    // ----- catch-branch closers (BowireMcpTools.cs:176 + BowireMockHandleRegistry.cs:57) -----

    [Fact]
    public async Task Subscribe_Window_Elapsed_Returns_Collected_Frames()
    {
        // Stream yields one frame then blocks indefinitely. With a 100 ms
        // window the linked CTS cancels mid-await, InvokeStreamAsync throws
        // OperationCanceledException, the Subscribe catch on line 176 swallows
        // it, and the collected frame is returned.
        var registry = new BowireProtocolRegistry();
        registry.Register(new BlockingStreamProtocol("blockingstream", "Blocking"));
        var tools = BuildTools(registry: registry,
            options: new BowireMcpOptions
            {
                AllowArbitraryUrls = true,
                LoadAllowlistFromEnvironments = false
            });

        var json = await tools.Subscribe(
            url: "http://localhost",
            service: "irrelevant",
            method: "irrelevant",
            protocol: "blockingstream",
            durationMs: 100,
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("frameCount").GetInt32());
        Assert.Equal("first", doc.RootElement.GetProperty("frames")[0].GetString());
    }

    [Fact]
    public async Task DisposeAsync_Swallows_Throwing_Handle()
    {
        // RegisterRaw lets us inject a faulty IAsyncDisposable so the catch on
        // line 57 of BowireMockHandleRegistry fires. The throwing entry must
        // not break the loop — the other (well-behaved) entry has to dispose
        // afterwards.
        var registry = NewMockHandles();
        // CA2000: the registry takes ownership and disposes both entries
        // inside its own DisposeAsync — that's exactly what this test
        // exercises.
#pragma warning disable CA2000
        var good = new TrackingDisposable();
        registry.RegisterRaw(new ThrowOnDisposable());
        registry.RegisterRaw(good);
#pragma warning restore CA2000

        await registry.DisposeAsync();

        Assert.True(good.Disposed);
    }

    private sealed class BlockingStreamProtocol : IBowireProtocol
    {
        public BlockingStreamProtocol(string id, string name) { Id = id; Name = name; }
        public string Id { get; }
        public string Name { get; }
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", []));

        public async IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return "first";
            // Block until cancelled so the linked CTS's CancelAfter fires.
            await Task.Delay(Timeout.Infinite, ct);
        }

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    private sealed class ThrowOnDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
            => throw new InvalidOperationException("intentional dispose failure");
    }

    private sealed class TrackingDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
