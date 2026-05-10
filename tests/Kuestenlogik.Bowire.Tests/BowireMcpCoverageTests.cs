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
/// Serialises tests that mutate <c>~/.bowire/{environments,recordings}.json</c>
/// — see <see cref="BowireMcpCoverageTests"/>. xUnit requires the
/// collection-definition class to be public.
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
/// Tests that touch <c>~/.bowire/{environments,recordings}.json</c>
/// are serialized via <see cref="BowireConfigFixture"/> because
/// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
/// for <c>UserProfile</c> ignores the <c>USERPROFILE</c> env var on
/// Windows, so each test must back up and restore the real file under
/// the user's profile.
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
    /// Backup-and-replace the <c>~/.bowire/{filename}</c> file with
    /// <paramref name="newContents"/> for the duration of <paramref name="action"/>,
    /// then restore the original. <c>Environment.GetFolderPath(UserProfile)</c>
    /// ignores <c>USERPROFILE</c> on Windows, so this is the only way to
    /// drive the file-present branches without refactoring production
    /// code. Serialised by <see cref="BowireConfigFixture"/>.
    /// </summary>
    private static void WithSwappedConfigFile(string filename, string newContents, Action action)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".bowire");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, filename);

        byte[]? backup = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try
        {
            File.WriteAllText(path, newContents);
            action();
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                File.WriteAllBytes(path, backup);
            }
        }
    }

    /// <summary>
    /// Move <c>~/.bowire/{filename}</c> aside (if present) for the duration
    /// of <paramref name="action"/> so the file-missing branches can be
    /// exercised regardless of the current user's <c>~/.bowire</c> state.
    /// </summary>
    private static void WithRemovedConfigFile(string filename, Action action)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".bowire");
        var path = Path.Combine(dir, filename);
        byte[]? backup = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try
        {
            if (backup is not null) File.Delete(path);
            action();
        }
        finally
        {
            if (backup is not null)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, backup);
            }
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
}
