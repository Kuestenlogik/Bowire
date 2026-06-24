// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mcp;
using Kuestenlogik.Bowire.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Mcp.Tests;

/// <summary>
/// Coverage for the #285 MCP record-session tools — bowire.record.start /
/// stop / replay. The pre-#285 sentinel tests in
/// <see cref="BowireMcpToolsTests"/> are gone; this file replaces them
/// with happy-path coverage backed by a shared <see cref="BowireRecordingSession"/>
/// singleton (mimicking what DI would hand the tool class in
/// production). Persistence is redirected to a temp <c>~/.bowire/</c>
/// via <see cref="BowireMcpTools.HomeDirOverride"/> so the suite never
/// touches the developer's real recordings.json.
/// </summary>
[Collection(nameof(BowireConfigFixture))]
public sealed class BowireMcpRecordSessionTests : IAsyncDisposable
{
    private readonly List<BowireMockHandleRegistry> _registries = [];
    private readonly string _tempHome;
    private readonly string? _previousHomeDirOverride;

    public BowireMcpRecordSessionTests()
    {
        _previousHomeDirOverride = BowireMcpTools.HomeDirOverride;
        _tempHome = Path.Combine(Path.GetTempPath(), $"bowire-mcp-record-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempHome, ".bowire"));
        BowireMcpTools.HomeDirOverride = _tempHome;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _registries) await r.DisposeAsync();
        BowireMcpTools.HomeDirOverride = _previousHomeDirOverride;
        try { if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private (BowireMcpTools tools, BowireRecordingSession session, BowireMcpConfirmationStore confirmations) BuildTools(
        bool requireConfirmation = false)
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            RequireConfirmationForMutations = requireConfirmation,
        };
        var mockHandles = new BowireMockHandleRegistry();
        _registries.Add(mockHandles);
        var session = new BowireRecordingSession();
        var confirmations = new BowireMcpConfirmationStore();
        var tools = new BowireMcpTools(
            new BowireProtocolRegistry(),
            mockHandles,
            confirmations,
            session,
            Options.Create(options),
            NullLogger<BowireMcpTools>.Instance);
        return (tools, session, confirmations);
    }

    // ----------- record.start -----------

    [Fact]
    public void RecordStart_Starts_Session_When_Confirm_True()
    {
        var (tools, session, _) = BuildTools();

        var json = tools.RecordStart(workspaceId: "ws-1", name: "alpha", confirm: true);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("started").GetBoolean());
        Assert.StartsWith("rec_", doc.RootElement.GetProperty("recordingId").GetString(), StringComparison.Ordinal);
        Assert.Equal("ws-1", doc.RootElement.GetProperty("workspaceId").GetString());
        Assert.Equal("alpha", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("capture", doc.RootElement.GetProperty("mode").GetString());

        Assert.NotNull(session.Active);
        Assert.Equal("ws-1", session.Active!.WorkspaceId);
    }

    [Theory]
    [InlineData("capture", BowireRecordingMode.Capture)]
    [InlineData("CAPTURE", BowireRecordingMode.Capture)]
    [InlineData("proxy", BowireRecordingMode.Proxy)]
    [InlineData("replay", BowireRecordingMode.Replay)]
    [InlineData("bogus", BowireRecordingMode.Capture)] // unknown falls back to capture
    [InlineData(null, BowireRecordingMode.Capture)]
    public void RecordStart_ParsesMode(string? mode, BowireRecordingMode expected)
    {
        var (tools, session, _) = BuildTools();
        _ = tools.RecordStart(workspaceId: "ws", mode: mode, confirm: true);
        Assert.NotNull(session.Active);
        Assert.Equal(expected, session.Active!.Mode);
    }

    [Fact]
    public void RecordStart_Confirmation_Gate_Returns_Pending_Token()
    {
        var (tools, _, _) = BuildTools(requireConfirmation: true);

        var json = tools.RecordStart(workspaceId: "ws", name: "alpha");

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("pending").GetBoolean());
        Assert.Equal(12, doc.RootElement.GetProperty("confirmationToken").GetString()!.Length);
        Assert.Contains("alpha", doc.RootElement.GetProperty("plan").GetString());
    }

    [Fact]
    public void RecordStart_Confirmation_Token_Redeems_Pending_Call()
    {
        var (tools, session, confirmations) = BuildTools(requireConfirmation: true);

        var first = tools.RecordStart(workspaceId: "ws", name: "alpha");
        using var firstDoc = JsonDocument.Parse(first);
        var token = firstDoc.RootElement.GetProperty("confirmationToken").GetString();

        var second = tools.RecordStart(workspaceId: "ws", name: "alpha", confirmationToken: token);
        using var secondDoc = JsonDocument.Parse(second);

        Assert.True(secondDoc.RootElement.GetProperty("started").GetBoolean());
        Assert.NotNull(session.Active);
        Assert.Equal(0, confirmations.Count); // token consumed
    }

    [Fact]
    public void RecordStart_When_Session_Already_Active_Returns_Error()
    {
        var (tools, _, _) = BuildTools();
        _ = tools.RecordStart(workspaceId: "ws", confirm: true);

        var second = tools.RecordStart(workspaceId: "ws", confirm: true);
        using var doc = JsonDocument.Parse(second);
        Assert.False(doc.RootElement.GetProperty("started").GetBoolean());
        Assert.Contains("already active",
            doc.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    // ----------- record.stop -----------

    [Fact]
    public void RecordStop_Without_Active_Session_Returns_No_Active_Session()
    {
        var (tools, _, _) = BuildTools();

        var json = tools.RecordStop(confirm: true);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("stopped").GetBoolean());
        Assert.Equal("no-active-session", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void RecordStop_Flushes_Active_Session_To_Recordings_Json()
    {
        var (tools, session, _) = BuildTools();
        _ = tools.RecordStart(workspaceId: "ws", name: "alpha", confirm: true);
        // Inject a step directly through the session so we can verify
        // the recording lands on disk.
        session.AppendStep(new Kuestenlogik.Bowire.Mocking.BowireRecordingStep
        {
            Id = "s1",
            Service = "Foo",
            Method = "Bar",
        });

        var json = tools.RecordStop(confirm: true);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("stopped").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("stepCount").GetInt32());
        Assert.Equal("alpha", doc.RootElement.GetProperty("name").GetString());
        Assert.Null(session.Active);

        // Persisted on disk under ~/.bowire/recordings.json in the
        // wrapper shape.
        var path = Path.Combine(_tempHome, ".bowire", "recordings.json");
        Assert.True(File.Exists(path));
        using var disk = JsonDocument.Parse(File.ReadAllText(path));
        var recordings = disk.RootElement.GetProperty("recordings");
        Assert.Equal(1, recordings.GetArrayLength());
        Assert.Equal("alpha", recordings[0].GetProperty("name").GetString());
    }

    [Fact]
    public void RecordStop_Confirmation_Gate_Returns_Pending_Token()
    {
        var (tools, _, _) = BuildTools(requireConfirmation: true);
        var json = tools.RecordStop();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("pending").GetBoolean());
    }

    // ----------- record.replay -----------

    [Fact]
    public void RecordReplay_Without_RecordingId_Returns_Error()
    {
        var (tools, _, _) = BuildTools();
        var json = tools.RecordReplay(recordingId: null, confirm: true);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("replaying").GetBoolean());
        Assert.Contains("recordingId is required",
            doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void RecordReplay_Opens_New_Session_When_Idle()
    {
        var (tools, session, _) = BuildTools();

        var json = tools.RecordReplay(workspaceId: "ws", recordingId: "rec_target", confirm: true);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("replaying").GetBoolean());
        Assert.Equal("rec_target", doc.RootElement.GetProperty("recordingId").GetString());
        Assert.Equal("replay", doc.RootElement.GetProperty("mode").GetString());

        Assert.NotNull(session.Active);
        Assert.Equal(BowireRecordingMode.Replay, session.Active!.Mode);
        Assert.Equal("rec_target", session.Active.RecordingId);
    }

    [Fact]
    public void RecordReplay_Switches_Existing_Capture_Session_Into_Replay()
    {
        var (tools, session, _) = BuildTools();
        _ = tools.RecordStart(workspaceId: "ws", confirm: true);
        var originalRecordingId = session.Active!.RecordingId;

        // Even though replay was called with a different recordingId, the
        // session is already active so we flip its mode instead.
        var json = tools.RecordReplay(workspaceId: "ws", recordingId: "rec_other", confirm: true);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("replaying").GetBoolean());
        Assert.Equal(BowireRecordingMode.Replay, session.Active!.Mode);
        // RecordingId remains the original session's id — switch-to-replay
        // doesn't rebind.
        Assert.Equal(originalRecordingId, session.Active.RecordingId);
    }

    [Fact]
    public void RecordReplay_Confirmation_Gate_Returns_Pending_Token()
    {
        var (tools, _, _) = BuildTools(requireConfirmation: true);
        var json = tools.RecordReplay(workspaceId: "ws", recordingId: "rec_x");
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("pending").GetBoolean());
        Assert.Contains("rec_x", doc.RootElement.GetProperty("plan").GetString());
    }
}
