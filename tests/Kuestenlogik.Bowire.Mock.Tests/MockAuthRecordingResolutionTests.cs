// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Management;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// #563: the apply-time resolution of a mock auth requirement's
/// <c>authRecordingId</c> into a concrete credential. Exercises
/// <see cref="MockAuthRecordingResolution.Apply"/> — the seam the config-apply
/// endpoint calls — across every outcome, including the fail-closed guards.
/// </summary>
public sealed class MockAuthRecordingResolutionTests
{
    private sealed class StubResolver(MockAuthResolution? result) : IAuthRecordingResolver
    {
        public string? LastId { get; private set; }
        public string? LastWorkspaceId { get; private set; }
        public MockAuthResolution? TryResolve(string authRecordingId, string? workspaceId)
        {
            LastId = authRecordingId;
            LastWorkspaceId = workspaceId;
            return result;
        }
    }

    [Fact]
    public void No_Auth_Block_Is_NoRecordingRef()
    {
        var config = new MockConfiguration();
        Assert.Equal(MockAuthRecordingResolution.Outcome.NoRecordingRef,
            MockAuthRecordingResolution.Apply(config, new StubResolver(new MockAuthResolution("x")), workspaceId: null));
    }

    [Fact]
    public void Auth_Without_RecordingId_Is_NoRecordingRef()
    {
        var config = new MockConfiguration { Auth = new MockAuthRequirement { Required = true, Credential = "direct" } };
        Assert.Equal(MockAuthRecordingResolution.Outcome.NoRecordingRef,
            MockAuthRecordingResolution.Apply(config, new StubResolver(new MockAuthResolution("x")), workspaceId: null));
        // The directly-configured credential is left untouched.
        Assert.Equal("direct", config.Auth!.Credential);
    }

    [Fact]
    public void RecordingId_But_Null_Resolver_Is_NoResolver()
    {
        var config = new MockConfiguration { Auth = new MockAuthRequirement { Required = true, AuthRecordingId = "rec-1" } };
        Assert.Equal(MockAuthRecordingResolution.Outcome.NoResolver,
            MockAuthRecordingResolution.Apply(config, resolver: null, workspaceId: null));
    }

    [Fact]
    public void RecordingId_Not_Found_Is_NotFound()
    {
        var config = new MockConfiguration { Auth = new MockAuthRequirement { Required = true, AuthRecordingId = "rec-1" } };
        var resolver = new StubResolver(result: null);
        Assert.Equal(MockAuthRecordingResolution.Outcome.NotFound,
            MockAuthRecordingResolution.Apply(config, resolver, workspaceId: "ws-7"));
        Assert.Equal("rec-1", resolver.LastId);
        Assert.Equal("ws-7", resolver.LastWorkspaceId);   // workspace is threaded through
    }

    [Fact]
    public void Resolved_Empty_Credential_Is_NotFound_Not_A_Presence_Only_Downgrade()
    {
        // A resolver that hands back an empty credential must FAIL CLOSED — an
        // empty credential would arm the gate in presence-only mode.
        var config = new MockConfiguration { Auth = new MockAuthRequirement { Required = true, AuthRecordingId = "rec-1" } };
        Assert.Equal(MockAuthRecordingResolution.Outcome.NotFound,
            MockAuthRecordingResolution.Apply(config, new StubResolver(new MockAuthResolution("")), workspaceId: null));
        // The config credential was NOT set to the empty string.
        Assert.True(string.IsNullOrEmpty(config.Auth!.Credential));
    }

    [Fact]
    public void Resolved_Populates_Credential_Scheme_And_Header()
    {
        var config = new MockConfiguration { Auth = new MockAuthRequirement { Required = true, AuthRecordingId = "rec-1" } };
        var resolver = new StubResolver(new MockAuthResolution("captured-tok", "apikey", "X-API-Key"));

        Assert.Equal(MockAuthRecordingResolution.Outcome.Resolved,
            MockAuthRecordingResolution.Apply(config, resolver, workspaceId: null));

        Assert.Equal("captured-tok", config.Auth!.Credential);
        Assert.Equal("apikey", config.Auth.Scheme);
        Assert.Equal("X-API-Key", config.Auth.Header);
    }

    [Fact]
    public void Resolved_Without_Scheme_Or_Header_Keeps_The_Config_Values()
    {
        var config = new MockConfiguration
        {
            Auth = new MockAuthRequirement { Required = true, Scheme = "bearer", Header = "Authorization", AuthRecordingId = "rec-1" },
        };
        var resolver = new StubResolver(new MockAuthResolution("captured-tok"));

        MockAuthRecordingResolution.Apply(config, resolver, workspaceId: null);

        Assert.Equal("captured-tok", config.Auth!.Credential);
        Assert.Equal("bearer", config.Auth.Scheme);          // unchanged
        Assert.Equal("Authorization", config.Auth.Header);   // unchanged
    }
}
