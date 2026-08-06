// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
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
public sealed class AuthRecordingMcpToolsTests : IAsyncDisposable
{
    private readonly List<BowireMockHandleRegistry> _registries = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var registry in _registries)
        {
            await registry.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }

    private BowireMcpTools Tools(bool requireConfirmation = false)
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
            NullLogger<BowireMcpTools>.Instance);
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
}
