// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Mcp;
using Kuestenlogik.Bowire.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Mcp.Tests;

/// <summary>
/// #563: the bowire.auth-recording.* MCP tools — CLI/UI/MCP parity for
/// auth-recording management. Covers the tool-specific logic (input validation,
/// the two-step confirmation gate, JSON shapes) without writing to the store;
/// the store round-trip itself is exercised by AuthRecordingStoreTests and the
/// endpoint integration tests.
/// </summary>
[Collection(nameof(BowireConfigFixture))]
public sealed class AuthRecordingMcpToolsTests : IAsyncDisposable
{
    private readonly List<BowireMockHandleRegistry> _registries = [];

    // The store resolves through BowireUserContext, so the tests that go past
    // the gate would otherwise write real credentials into the developer's own
    // ~/.bowire. Redirected here, restored on the way out; the collection
    // serialises it because the context is a process-wide static.
    private readonly string _home = SafePath.Combine(
        Path.GetTempPath(), $"bowire-mcp-authrec-{Guid.NewGuid():N}");
    private readonly IBowireUserStore _previousUserStore = BowireUserContext.Current;

    public AuthRecordingMcpToolsTests()
    {
        Directory.CreateDirectory(_home);
        BowireUserContext.Current = new DefaultBowireUserStore(_home);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var registry in _registries)
        {
            await registry.DisposeAsync();
        }
        BowireUserContext.Current = _previousUserStore;
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private BowireMcpTools Tools(bool requireConfirmation = false, Kuestenlogik.Bowire.Mocking.IAuthFlowCapturer? capturer = null)
    {
        var handles = new BowireMockHandleRegistry();
        _registries.Add(handles);
        return new BowireMcpTools(
            new BowireProtocolRegistry(),
            handles,
            new BowireMcpConfirmationStore(),
            new BowireRecordingSession(),
            Options.Create(new BowireMcpOptions
            {
                LoadAllowlistFromEnvironments = false,
                RequireConfirmationForMutations = requireConfirmation,
            }),
            NullLogger<BowireMcpTools>.Instance,
            capturer);
    }

    private sealed class FakeCapturer : Kuestenlogik.Bowire.Mocking.IAuthFlowCapturer
    {
        public Task<Kuestenlogik.Bowire.Mocking.AuthFlowCaptureResult> CaptureAsync(string flowJson, CancellationToken ct = default)
            => Task.FromResult(new Kuestenlogik.Bowire.Mocking.AuthFlowCaptureResult("captured-tok", "bearer", null));
    }

    [Fact]
    public async Task CaptureFlow_Without_A_Capturer_Is_Not_Available()
    {
        var result = await Tools().AuthRecordingCaptureFlow("rec-1", "{}", ct: TestContext.Current.CancellationToken);
        Assert.Contains("not available", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureFlow_Rejects_Empty_Id_And_Flow()
    {
        var tools = Tools(capturer: new FakeCapturer());
        Assert.Contains("id is required", await tools.AuthRecordingCaptureFlow(id: string.Empty, flow: "{}", ct: TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("flow is required", await tools.AuthRecordingCaptureFlow(id: "rec", flow: string.Empty, ct: TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureFlow_Parks_A_Confirmation_When_Gated()
    {
        // The gate must fire BEFORE the outbound flow runs.
        var json = await Tools(requireConfirmation: true, capturer: new FakeCapturer())
            .AuthRecordingCaptureFlow("rec-1", "{}", confirm: false, ct: TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("pending").GetBoolean());
        Assert.Equal("bowire.auth-recording.capture-flow", doc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public void Capture_Rejects_Empty_Id_And_Credential()
    {
        var tools = Tools();
        Assert.Contains("id is required", tools.AuthRecordingCapture(id: string.Empty, credential: "tok"), StringComparison.Ordinal);
        Assert.Contains("credential is required", tools.AuthRecordingCapture(id: "rec", credential: string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_Parks_A_Confirmation_When_Gated()
    {
        // With the confirmation gate on, the first call must NOT write — it parks
        // a plan and asks for a second-step confirm.
        var json = Tools(requireConfirmation: true).AuthRecordingCapture("rec-1", "tok", confirm: false);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("pending").GetBoolean());
        Assert.Equal("bowire.auth-recording.capture", doc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public void Remove_Parks_A_Confirmation_When_Gated()
    {
        var json = Tools(requireConfirmation: true).AuthRecordingRemove("rec-1", confirm: false);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("pending").GetBoolean());
    }

    [Fact]
    public void List_Fresh_Workspace_Is_Empty()
    {
        // A never-written, unique workspace resolves to no directory → empty list,
        // read-only (no disk write, no cross-test race on the shared home).
        var workspace = "authrec-mcp-" + Guid.NewGuid().ToString("N");
        using var doc = JsonDocument.Parse(BowireMcpTools.AuthRecordingList(workspace));
        Assert.Empty(doc.RootElement.GetProperty("recordings").EnumerateArray());
    }

    // ---- the confirmed path: what actually reaches the store ----
    //
    // The tests above stop at the gate. These carry on past it, because the
    // half that matters to an operator is what an agent left on their disk —
    // and the answer the agent gets back has to describe it without quoting
    // the credential.

    private static string Workspace() => "authrec-mcp-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void A_Confirmed_Capture_Stores_The_Recording_And_Says_So()
    {
        var workspace = Workspace();
        var tools = Tools();

        var json = tools.AuthRecordingCapture(
            "rec-1", "super-secret-token", name: "Production", scheme: "bearer", workspace: workspace);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("rec-1", doc.RootElement.GetProperty("id").GetString());
        // The credential never comes back out — an agent's transcript is not
        // a place for it, and the agent already had it.
        Assert.DoesNotContain("super-secret-token", json, StringComparison.Ordinal);

        using var listed = JsonDocument.Parse(BowireMcpTools.AuthRecordingList(workspace));
        var row = Assert.Single(listed.RootElement.GetProperty("recordings").EnumerateArray());
        Assert.Equal("rec-1", row.GetProperty("id").GetString());
        Assert.DoesNotContain("super-secret-token", listed.RootElement.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_Capture_Without_A_Scheme_Is_Stored_As_Bearer()
    {
        // The commonest case by far; making an agent spell it out every time
        // would be friction with no decision behind it.
        var workspace = Workspace();

        Tools().AuthRecordingCapture("rec-1", "tok", workspace: workspace);

        using var listed = JsonDocument.Parse(BowireMcpTools.AuthRecordingList(workspace));
        var row = Assert.Single(listed.RootElement.GetProperty("recordings").EnumerateArray());
        Assert.Equal("bearer", row.GetProperty("scheme").GetString());
    }

    [Fact]
    public void Removing_A_Stored_Recording_Takes_It_Off_The_List()
    {
        var workspace = Workspace();
        var tools = Tools();
        tools.AuthRecordingCapture("rec-1", "tok", workspace: workspace);

        var json = tools.AuthRecordingRemove("rec-1", workspace: workspace);

        Assert.DoesNotContain("\"pending\":true", json, StringComparison.Ordinal);
        using var listed = JsonDocument.Parse(BowireMcpTools.AuthRecordingList(workspace));
        Assert.Empty(listed.RootElement.GetProperty("recordings").EnumerateArray());
    }

    [Fact]
    public void Two_Workspaces_Can_Hold_The_Same_Id_Without_Colliding()
    {
        // Scoping is what lets a team keep a "prod-token" per workspace; one
        // answering for the other would be a credential mix-up, not a bug in
        // a list.
        var a = Workspace();
        var b = Workspace();
        var tools = Tools();
        tools.AuthRecordingCapture("prod-token", "tok-a", name: "A", workspace: a);
        tools.AuthRecordingCapture("prod-token", "tok-b", name: "B", workspace: b);

        using var listedA = JsonDocument.Parse(BowireMcpTools.AuthRecordingList(a));
        using var listedB = JsonDocument.Parse(BowireMcpTools.AuthRecordingList(b));

        Assert.Equal("A", listedA.RootElement.GetProperty("recordings")
            .EnumerateArray().Single().GetProperty("name").GetString());
        Assert.Equal("B", listedB.RootElement.GetProperty("recordings")
            .EnumerateArray().Single().GetProperty("name").GetString());

        tools.AuthRecordingRemove("prod-token", workspace: a);
        tools.AuthRecordingRemove("prod-token", workspace: b);
    }

    [Fact]
    public async Task A_Confirmed_Flow_Capture_Stores_What_The_Login_Chain_Returned()
    {
        // The flow's own answer decides the scheme and header — not the
        // arguments. An agent that guessed "bearer" for a cookie-based login
        // would store a credential the mock then presents the wrong way.
        var workspace = Workspace();
        var tools = Tools(capturer: new FakeCapturer());

        var json = await tools.AuthRecordingCaptureFlow(
            "rec-flow", "{}", name: "Login chain", workspace: workspace,
            ct: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("pending", json, StringComparison.Ordinal);
        Assert.DoesNotContain("captured-tok", json, StringComparison.Ordinal);

        using var listed = JsonDocument.Parse(BowireMcpTools.AuthRecordingList(workspace));
        var row = Assert.Single(listed.RootElement.GetProperty("recordings").EnumerateArray());
        Assert.Equal("rec-flow", row.GetProperty("id").GetString());
        Assert.Equal("bearer", row.GetProperty("scheme").GetString());
    }

    [Fact]
    public async Task A_Flow_That_Fails_Reports_It_And_Stores_Nothing()
    {
        // A login chain that cannot complete must not leave a half-recording
        // behind for a mock to resolve later.
        var workspace = Workspace();
        var tools = Tools(capturer: new FailingCapturer());

        var result = await tools.AuthRecordingCaptureFlow(
            "rec-flow", "{}", workspace: workspace, ct: TestContext.Current.CancellationToken);

        Assert.Contains("failed", result, StringComparison.Ordinal);
        using var listed = JsonDocument.Parse(BowireMcpTools.AuthRecordingList(workspace));
        Assert.Empty(listed.RootElement.GetProperty("recordings").EnumerateArray());
    }

    private sealed class FailingCapturer : Kuestenlogik.Bowire.Mocking.IAuthFlowCapturer
    {
        public Task<Kuestenlogik.Bowire.Mocking.AuthFlowCaptureResult> CaptureAsync(
            string flowJson, CancellationToken ct = default)
            => throw new Kuestenlogik.Bowire.Mocking.AuthFlowCaptureException("token step returned 401");
    }
}
