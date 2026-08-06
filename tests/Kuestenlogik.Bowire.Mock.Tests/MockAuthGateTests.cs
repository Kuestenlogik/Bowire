// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// #562: the mock auth gate — when a requirement demands a credential, a
/// request that presents none (or the wrong one) is not authorized.
/// </summary>
public sealed class MockAuthGateTests
{
    private static DefaultHttpContext Ctx(string? headerValue, string headerName = "Authorization")
    {
        var ctx = new DefaultHttpContext();
        if (headerValue is not null) ctx.Request.Headers[headerName] = headerValue;
        return ctx;
    }

    [Fact]
    public void No_Requirement_Or_Not_Required_Is_Authorized()
    {
        var gate = new MockAuthGate();
        Assert.True(gate.IsAuthorized(Ctx(null)));

        gate.Current = new MockAuthRequirement { Required = false };
        Assert.True(gate.IsAuthorized(Ctx(null)));
    }

    [Fact]
    public void Required_Missing_Credential_Is_Unauthorized()
    {
        var gate = new MockAuthGate { Current = new MockAuthRequirement { Required = true, Scheme = "bearer", Credential = "tok" } };
        Assert.False(gate.IsAuthorized(Ctx(null)));
    }

    [Fact]
    public void Required_Correct_Bearer_Is_Authorized_Wrong_Is_Not()
    {
        var gate = new MockAuthGate { Current = new MockAuthRequirement { Required = true, Scheme = "bearer", Credential = "tok" } };
        Assert.True(gate.IsAuthorized(Ctx("Bearer tok")));
        Assert.False(gate.IsAuthorized(Ctx("Bearer nope")));
    }

    [Fact]
    public void Presence_Only_Accepts_Any_NonEmpty_Credential()
    {
        // No expected Credential → any non-empty credential of the scheme passes.
        var gate = new MockAuthGate { Current = new MockAuthRequirement { Required = true, Scheme = "bearer" } };
        Assert.True(gate.IsAuthorized(Ctx("Bearer anything")));
        Assert.False(gate.IsAuthorized(Ctx(null)));
    }

    [Fact]
    public void ApiKey_Reads_The_Named_Header_Raw()
    {
        var gate = new MockAuthGate { Current = new MockAuthRequirement { Required = true, Scheme = "apikey", Header = "X-API-Key", Credential = "k1" } };
        Assert.True(gate.IsAuthorized(Ctx("k1", "X-API-Key")));
        Assert.False(gate.IsAuthorized(Ctx("wrong", "X-API-Key")));
        Assert.False(gate.IsAuthorized(Ctx("k1", "Authorization"))); // key not in the named header
    }

    [Fact]
    public void Basic_Scheme_Strips_The_Prefix_And_Matches()
    {
        var gate = new MockAuthGate { Current = new MockAuthRequirement { Required = true, Scheme = "basic", Credential = "dXNlcjpwYXNz" } };
        Assert.True(gate.IsAuthorized(Ctx("Basic dXNlcjpwYXNz")));
        Assert.False(gate.IsAuthorized(Ctx("Basic wrong")));
    }

    [Fact]
    public void Bare_Scheme_Word_Is_Not_A_Credential()
    {
        // `Authorization: Bearer` with no token must not satisfy presence-only —
        // there is no credential to present.
        var gate = new MockAuthGate { Current = new MockAuthRequirement { Required = true, Scheme = "bearer" } };
        Assert.False(gate.IsAuthorized(Ctx("Bearer")));
        Assert.False(gate.IsAuthorized(Ctx("Bearer ")));
    }

    [Fact]
    public void RequireBearer_Maps_The_Cli_Flag()
    {
        // The --require-auth mapping: empty/null → open gate; a token →
        // a bearer requirement demanding exactly that token.
        Assert.Null(MockAuthGate.RequireBearer(null).Current);
        Assert.Null(MockAuthGate.RequireBearer("").Current);

        var gate = MockAuthGate.RequireBearer("tok");
        Assert.NotNull(gate.Current);
        Assert.True(gate.Current!.Required);
        Assert.Equal("bearer", gate.Current.Scheme);
        Assert.Equal("tok", gate.Current.Credential);
        Assert.True(gate.IsAuthorized(Ctx("Bearer tok")));
        Assert.False(gate.IsAuthorized(Ctx(null)));
    }

    [Fact]
    public void Swapping_Current_Toggles_The_Gate_Live()
    {
        var gate = new MockAuthGate { Current = new MockAuthRequirement { Required = true, Credential = "tok" } };
        Assert.False(gate.IsAuthorized(Ctx(null)));
        gate.Current = null; // toggled off
        Assert.True(gate.IsAuthorized(Ctx(null)));
    }
}
