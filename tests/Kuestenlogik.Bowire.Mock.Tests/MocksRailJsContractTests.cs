// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Management;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// JS-side contract pins for the Mock rail fragment (mocks.js, embedded in
/// Kuestenlogik.Bowire.Mock). Bowire has no JS test runner, so string
/// invariants over the embedded source fail loudly when the #560 schema-mock
/// contract drifts — the config-artifact half (seedMockConfig) is otherwise
/// only exercised by manual QA.
/// </summary>
public sealed class MocksRailJsContractTests
{
    private static readonly Lazy<string> Fragment = new(LoadFragment);

    [Fact]
    public void Exposes_StartFromSchema_On_The_Shim()
    {
        // Any host driving a schema mock reaches it via
        // window.__bowireMocks.startFromSchema; dropping the export silently
        // breaks the rail's schema-mock entry point.
        Assert.Contains("startFromSchema: startMockFromSchema", Fragment.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void StartFromSchema_Posts_The_Schema_Start_Shape()
    {
        var js = Fragment.Value;
        Assert.Contains("function startMockFromSchema", js, StringComparison.Ordinal);
        Assert.Contains("schemaKind: schemaKind", js, StringComparison.Ordinal);
        Assert.Contains("schemaInline: schemaInline", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Seeds_The_Mock_Config_Artifact_After_A_Successful_Start()
    {
        // #560 acceptance: starting a schema mock creates its config artifact.
        // startMockFromSchema must call seedMockConfig, which PUTs to
        // /api/mocks/{id}/config carrying the workspace id — the target the
        // #561 refinement editors read back. This pin is the only automated
        // guard on that glue.
        var js = Fragment.Value;
        Assert.Contains("function seedMockConfig", js, StringComparison.Ordinal);
        Assert.Contains("seedMockConfig(summary.mockId", js, StringComparison.Ordinal);
        Assert.Contains("encodeURIComponent(mockId) + '/config'", js, StringComparison.Ordinal);
        Assert.Contains("method: 'PUT'", js, StringComparison.Ordinal);
        Assert.Contains("workspaceId=", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Ships_The_Schema_Mock_Refinement_Editors()
    {
        // #561: the two editor cards + the apply flow (persist to the store
        // AND apply live to the running mock). Dropping either card or the
        // /config/apply POST silently breaks the editors.
        var js = Fragment.Value;
        Assert.Contains("function renderOverridesCard", js, StringComparison.Ordinal);
        Assert.Contains("function renderRulesCard", js, StringComparison.Ordinal);
        Assert.Contains("function applyMockConfig", js, StringComparison.Ordinal);
        Assert.Contains("'/config/apply'", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Ships_The_Require_Auth_Toggle()
    {
        // #562: the require-auth card + toggle, applied through the #561 flow.
        var js = Fragment.Value;
        Assert.Contains("function renderAuthCard", js, StringComparison.Ordinal);
        Assert.Contains("Require authentication", js, StringComparison.Ordinal);

        // The card is only wired up if it is mounted into the detail pane AND
        // serializeMockConfig actually carries `auth` to the wire — dropping
        // either silently disables the toggle while every backend test stays
        // green, so pin both glue points here (the only automated guard).
        Assert.Contains("wrap.appendChild(renderAuthCard(selected))", js, StringComparison.Ordinal);
        Assert.Contains("auth: st.config.auth", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Ships_The_Auth_Recording_Picker()
    {
        // #563: the auth-card recording picker fetches the workspace's
        // recordings and binds the selection to auth.authRecordingId.
        var js = Fragment.Value;
        Assert.Contains("function loadAuthRecordings", js, StringComparison.Ordinal);
        Assert.Contains("'/api/auth-recordings'", js, StringComparison.Ordinal);
        Assert.Contains("st.config.auth.authRecordingId = v", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Ships_Inline_Auth_Recording_Create_And_Remove()
    {
        // #563 CLI/UI/MCP parity: the auth card can create + remove recordings
        // inline (no hand-writing JSON), PUT/DELETE-ing the same store the CLI
        // and MCP tools use.
        var js = Fragment.Value;
        Assert.Contains("function saveAuthRecording", js, StringComparison.Ordinal);
        Assert.Contains("function deleteAuthRecording", js, StringComparison.Ordinal);
        Assert.Contains("'+ New recording'", js, StringComparison.Ordinal);
    }

    private static string LoadFragment()
    {
        var assembly = typeof(BowireMockManagementEndpoints).Assembly;
        const string resourceName = "Kuestenlogik.Bowire.Mock.wwwroot.js.mocks.js";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
