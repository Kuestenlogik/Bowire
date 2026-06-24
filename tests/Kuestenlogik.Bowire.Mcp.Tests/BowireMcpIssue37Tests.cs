// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mcp;
using Kuestenlogik.Bowire.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Mcp.Tests;

/// <summary>
/// Coverage for the #37 feature set — bowire.har.import, bowire.assert,
/// bowire.allowlist.permit, the two-step confirmation gate on mutator
/// tools, and the typed-URL allowlist seed. Each test pins
/// <see cref="BowireMcpTools.HomeDirOverride"/> at a temp folder so it
/// never sees the developer's real <c>~/.bowire/</c>.
/// </summary>
[Collection(nameof(BowireConfigFixture))]
public sealed class BowireMcpIssue37Tests : IAsyncDisposable
{
    private readonly List<BowireMockHandleRegistry> _registries = [];
    private readonly List<string> _tempDirs = [];
    private readonly string? _previousHomeDirOverride;
    private readonly string? _previousTypedUrlOverride;
    private readonly string _tempHome;

    public BowireMcpIssue37Tests()
    {
        _previousHomeDirOverride = BowireMcpTools.HomeDirOverride;
        _previousTypedUrlOverride = BowireMcpTypedUrlStore.HomeDirOverride;
        _tempHome = Path.Combine(Path.GetTempPath(), $"bowire-mcp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempHome, ".bowire"));
        BowireMcpTools.HomeDirOverride = _tempHome;
        BowireMcpTypedUrlStore.HomeDirOverride = _tempHome;
        _tempDirs.Add(_tempHome);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _registries) await r.DisposeAsync();
        BowireMcpTools.HomeDirOverride = _previousHomeDirOverride;
        BowireMcpTypedUrlStore.HomeDirOverride = _previousTypedUrlOverride;
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
        GC.SuppressFinalize(this);
    }

    private BowireMcpTools BuildTools(
        BowireMcpOptions? options = null,
        BowireMcpConfirmationStore? confirmations = null)
    {
        options ??= new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            RequireConfirmationForMutations = false,
        };
        var registry = new BowireProtocolRegistry();
        var mockHandles = new BowireMockHandleRegistry();
        _registries.Add(mockHandles);
        confirmations ??= new BowireMcpConfirmationStore();
        return new BowireMcpTools(
            registry,
            mockHandles,
            confirmations,
            new Kuestenlogik.Bowire.Recording.BowireRecordingSession(),
            Options.Create(options),
            NullLogger<BowireMcpTools>.Instance);
    }

    // ---------------- bowire.har.import ----------------

    private const string MinimalHar = /*lang=json,strict*/ """
        {
          "log": {
            "version": "1.2",
            "creator": { "name": "Test", "version": "1" },
            "entries": [
              {
                "startedDateTime": "2026-04-01T10:00:00.000Z",
                "time": 12,
                "request": {
                  "method": "GET",
                  "url": "https://api.example.com/health",
                  "headers": []
                },
                "response": { "status": 200, "content": { "text": "{}" } }
              }
            ]
          }
        }
        """;

    [Fact]
    public async Task HarImport_With_OutPath_Writes_Recording_File_And_Returns_Summary()
    {
        var harPath = Path.Combine(_tempHome, "input.har");
        var outPath = Path.Combine(_tempHome, "output.bwr");
        await File.WriteAllTextAsync(harPath, MinimalHar, TestContext.Current.CancellationToken);

        var json = await BowireMcpTools.HarImport(harPath, outPath, name: "Health probe");

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("stepCount").GetInt32() == 1);
        Assert.Equal(Path.GetFullPath(outPath), doc.RootElement.GetProperty("outPath").GetString());
        Assert.StartsWith("rec_har_", doc.RootElement.GetProperty("recordingId").GetString());

        var written = await File.ReadAllTextAsync(outPath, TestContext.Current.CancellationToken);
        using var recDoc = JsonDocument.Parse(written);
        Assert.Equal("Health probe", recDoc.RootElement.GetProperty("name").GetString());
        Assert.Equal(1, recDoc.RootElement.GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task HarImport_Without_OutPath_Returns_Recording_Inline()
    {
        var harPath = Path.Combine(_tempHome, "input.har");
        await File.WriteAllTextAsync(harPath, MinimalHar, TestContext.Current.CancellationToken);

        var json = await BowireMcpTools.HarImport(harPath);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("recording", out var rec));
        Assert.Equal(1, rec.GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task HarImport_With_Missing_File_Returns_Error_String()
    {
        var result = await BowireMcpTools.HarImport(Path.Combine(_tempHome, "missing.har"));
        Assert.Contains("HAR file not found", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarImport_With_Malformed_Har_Returns_Import_Failed_Message()
    {
        var harPath = Path.Combine(_tempHome, "malformed.har");
        await File.WriteAllTextAsync(harPath, "{not json", TestContext.Current.CancellationToken);
        var result = await BowireMcpTools.HarImport(harPath);
        Assert.Contains("HAR import failed", result, StringComparison.Ordinal);
    }

    // ---------------- bowire.assert ----------------

    [Fact]
    public async Task Assert_Appends_Assertion_To_Step_In_Standalone_Recording_File()
    {
        var recordingPath = Path.Combine(_tempHome, "single.bwr");
        await File.WriteAllTextAsync(recordingPath, /*lang=json,strict*/ """
            {
              "id": "rec_test",
              "name": "test",
              "steps": [
                { "id": "step1", "protocol": "rest", "service": "Foo", "method": "Bar" }
              ]
            }
            """, TestContext.Current.CancellationToken);

        var expected = JsonDocument.Parse("\"OK\"").RootElement;
        var json = await BowireMcpTools.Assert(
            stepIndex: 0, path: "status", op: "eq",
            expected: expected, recordingPath: recordingPath);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("stepIndex").GetInt32());
        Assert.Equal("step1", doc.RootElement.GetProperty("stepId").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("assertionCount").GetInt32());
        Assert.StartsWith("t_", doc.RootElement.GetProperty("assertionId").GetString());

        // Persisted on disk under the step's assertions[] array.
        var disk = await File.ReadAllTextAsync(recordingPath, TestContext.Current.CancellationToken);
        using var diskDoc = JsonDocument.Parse(disk);
        var step = diskDoc.RootElement.GetProperty("steps")[0];
        var assertions = step.GetProperty("assertions");
        Assert.Equal(1, assertions.GetArrayLength());
        Assert.Equal("status", assertions[0].GetProperty("path").GetString());
        Assert.Equal("eq", assertions[0].GetProperty("op").GetString());
        Assert.Equal("OK", assertions[0].GetProperty("expected").GetString());
    }

    [Fact]
    public async Task Assert_Targets_Recording_In_Wrapper_File_By_Id()
    {
        var wrapperPath = Path.Combine(_tempHome, ".bowire", "recordings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(wrapperPath)!);
        await File.WriteAllTextAsync(wrapperPath, /*lang=json,strict*/ """
            {
              "recordings": [
                { "id": "r-A", "steps": [ {"id":"s1"} ] },
                { "id": "r-B", "steps": [ {"id":"s2"}, {"id":"s3"} ] }
              ]
            }
            """, TestContext.Current.CancellationToken);

        var expected = JsonDocument.Parse("42").RootElement;
        var json = await BowireMcpTools.Assert(
            stepIndex: 1, path: "$.count", op: "gt",
            expected: expected, recordingId: "r-B");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("s3", doc.RootElement.GetProperty("stepId").GetString());
        Assert.Equal("r-B", doc.RootElement.GetProperty("recordingId").GetString());

        var disk = await File.ReadAllTextAsync(wrapperPath, TestContext.Current.CancellationToken);
        using var diskDoc = JsonDocument.Parse(disk);
        var rB = diskDoc.RootElement.GetProperty("recordings")[1];
        Assert.Equal(1, rB.GetProperty("steps")[1].GetProperty("assertions").GetArrayLength());
    }

    [Fact]
    public async Task Assert_Rejects_Unknown_Op()
    {
        var p = Path.Combine(_tempHome, "rec.bwr");
        await File.WriteAllTextAsync(p, /*lang=json,strict*/ """{ "steps": [ {} ] }""", TestContext.Current.CancellationToken);
        var json = await BowireMcpTools.Assert(0, "x", "totally-bogus",
            JsonDocument.Parse("1").RootElement, recordingPath: p);
        Assert.Contains("unknown op", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Assert_Rejects_Both_Id_And_Path()
    {
        var json = await BowireMcpTools.Assert(0, "x", "eq",
            JsonDocument.Parse("1").RootElement,
            recordingId: "r1", recordingPath: "foo.bwr");
        Assert.Contains("exactly one of", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Assert_Out_Of_Range_Step_Index_Returns_Error()
    {
        var p = Path.Combine(_tempHome, "rec.bwr");
        await File.WriteAllTextAsync(p, /*lang=json,strict*/ """{ "steps": [ {} ] }""", TestContext.Current.CancellationToken);
        var json = await BowireMcpTools.Assert(5, "x", "eq",
            JsonDocument.Parse("1").RootElement, recordingPath: p);
        Assert.Contains("out of range", json, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- bowire.allowlist.permit ----------------

    [Fact]
    public void AllowlistPermit_Persists_To_File_And_Updates_Memory()
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            RequireConfirmationForMutations = false,
        };
        var tools = BuildTools(options);

        var json = tools.AllowlistPermit("https://api.example.com/v1");

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("addedToFile").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("addedToMemory").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("allowlistSize").GetInt32());

        Assert.Contains("https://api.example.com/v1", options.AllowedServerUrls);
        Assert.Contains("https://api.example.com/v1", BowireMcpTypedUrlStore.LoadAll());
    }

    [Fact]
    public void AllowlistPermit_Idempotent_On_Repeat()
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            RequireConfirmationForMutations = false,
        };
        var tools = BuildTools(options);

        _ = tools.AllowlistPermit("https://api.example.com");
        var second = tools.AllowlistPermit("https://api.example.com");

        using var doc = JsonDocument.Parse(second);
        Assert.False(doc.RootElement.GetProperty("addedToFile").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("addedToMemory").GetBoolean());
        Assert.Single(options.AllowedServerUrls);
    }

    // ---------------- Typed-URL allowlist seed ----------------

    [Fact]
    public void Ctor_LoadAllowlistFromTypedUrls_Adds_Urls_From_File()
    {
        File.WriteAllText(
            BowireMcpTypedUrlStore.FilePath,
            /*lang=json,strict*/ """["http://localhost:7000","https://typed.example.com"]""");

        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            LoadAllowlistFromTypedUrls = true,
            RequireConfirmationForMutations = false,
        };
        _ = BuildTools(options);

        Assert.Contains("http://localhost:7000", options.AllowedServerUrls);
        Assert.Contains("https://typed.example.com", options.AllowedServerUrls);
    }

    [Fact]
    public void Ctor_LoadAllowlistFromTypedUrls_Ignores_Missing_File()
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            LoadAllowlistFromTypedUrls = true,
            RequireConfirmationForMutations = false,
        };
        _ = BuildTools(options);
        Assert.Empty(options.AllowedServerUrls);
    }

    // ---------------- Confirmation gate ----------------

    [Fact]
    public async Task MockStart_With_Confirmation_Required_Returns_Pending_Token()
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            RequireConfirmationForMutations = true,
        };
        var tools = BuildTools(options);

        var json = await tools.MockStart(
            recording: "some-path.json",
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("pending").GetBoolean());
        Assert.Equal(12, doc.RootElement.GetProperty("confirmationToken").GetString()!.Length);
        Assert.Contains("Start a mock server", doc.RootElement.GetProperty("plan").GetString());
    }

    [Fact]
    public async Task MockStart_With_Confirm_True_Bypasses_Pending_And_Attempts_Start()
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            RequireConfirmationForMutations = true,
        };
        var tools = BuildTools(options);

        // Non-existent path → MockServer.StartAsync fails. The point of
        // this test is that the *confirmation gate* is bypassed; we see
        // the failure-from-mock-server response, not the pending shape.
        var json = await tools.MockStart(
            recording: Path.Combine(_tempHome, "does-not-exist.json"),
            confirm: true,
            ct: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("\"pending\":true", json, StringComparison.Ordinal);
        Assert.Contains("bowire.mock.start failed", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MockStart_Confirmation_Token_From_Prior_Call_Is_Redeemed()
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            RequireConfirmationForMutations = true,
        };
        var confirmations = new BowireMcpConfirmationStore();
        var tools = BuildTools(options, confirmations);

        var first = await tools.MockStart(
            recording: Path.Combine(_tempHome, "x.json"),
            ct: TestContext.Current.CancellationToken);

        using var firstDoc = JsonDocument.Parse(first);
        var token = firstDoc.RootElement.GetProperty("confirmationToken").GetString();

        var second = await tools.MockStart(
            recording: Path.Combine(_tempHome, "x.json"),
            confirmationToken: token,
            ct: TestContext.Current.CancellationToken);

        // Token consumed — recording path doesn't exist, so we land in
        // the underlying failure (proving the gate let us through).
        Assert.DoesNotContain("\"pending\":true", second, StringComparison.Ordinal);
        Assert.Contains("failed", second, StringComparison.Ordinal);
        Assert.Equal(0, confirmations.Count);
    }

    [Fact]
    public async Task MockStart_With_Unknown_Confirmation_Token_Returns_Error()
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            RequireConfirmationForMutations = true,
        };
        var tools = BuildTools(options);

        var json = await tools.MockStart(
            recording: "any.json",
            confirmationToken: "deadbeefcafe",
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(json);
        Assert.Contains("No pending confirmation matches",
            doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void RecordStart_With_Confirmation_Required_Returns_Pending_Token()
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            RequireConfirmationForMutations = true,
        };
        var tools = BuildTools(options);

        var json = tools.RecordStart(name: "my-rec");

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("pending").GetBoolean());
        Assert.Contains("my-rec", doc.RootElement.GetProperty("plan").GetString());
    }

    // ---------------- AllowlistShow now reports the new toggles ----------------

    [Fact]
    public void AllowlistShow_Includes_New_Toggle_Fields()
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            LoadAllowlistFromTypedUrls = true,
            RequireConfirmationForMutations = false,
        };
        var tools = BuildTools(options);

        var json = tools.AllowlistShow();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("loadFromTypedUrls").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("requireConfirmationForMutations").GetBoolean());
    }
}
