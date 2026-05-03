// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.App;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Locks the wire shape of <c>TestCollection</c> / <c>TestEntry</c> /
/// <c>Assertion</c> against the JSON the in-browser Tests tab and the
/// CLI <c>bowire test</c> writer produce — both deserialise the same
/// document via the same <c>JsonNamingPolicy.CamelCase</c> options.
/// Renaming a property here without bumping the on-disk format would
/// silently break existing recordings, so this test sits between the
/// two sides as a contract guard.
/// </summary>
public sealed class TestCollectionShapeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Collection_Defaults_AreEmptyButNotNullForTests()
    {
        var c = new TestCollection();
        Assert.Null(c.Name);
        Assert.Null(c.ServerUrl);
        Assert.Null(c.Protocol);
        Assert.Null(c.Environment);
        Assert.NotNull(c.Tests);
        Assert.Empty(c.Tests);
    }

    [Fact]
    public void TestEntry_Defaults_AllNull()
    {
        var t = new TestEntry();
        Assert.Null(t.Name);
        Assert.Null(t.Service);
        Assert.Null(t.Method);
        Assert.Null(t.ServerUrl);
        Assert.Null(t.Protocol);
        Assert.Null(t.Messages);
        Assert.Null(t.Metadata);
        Assert.Null(t.Environment);
        Assert.Null(t.Assert);
    }

    [Fact]
    public void Assertion_Defaults_AllNull()
    {
        var a = new Assertion();
        Assert.Null(a.Path);
        Assert.Null(a.Op);
        Assert.Null(a.Expected);
    }

    [Fact]
    public void Deserialize_Minimal_RoundtripsName()
    {
        const string json = """
            {
              "name": "happy",
              "serverUrl": "http://api",
              "tests": []
            }
            """;
        var c = JsonSerializer.Deserialize<TestCollection>(json, JsonOptions);
        Assert.NotNull(c);
        Assert.Equal("happy", c!.Name);
        Assert.Equal("http://api", c.ServerUrl);
        Assert.Empty(c.Tests);
    }

    [Fact]
    public void Deserialize_NestedAssertions_PopulatesEverything()
    {
        const string json = """
            {
              "name": "regression",
              "protocol": "grpc",
              "environment": { "token": "abc" },
              "tests": [
                {
                  "name": "Login",
                  "service": "users.UserService",
                  "method": "Login",
                  "messages": ["{\"user\":\"alice\"}"],
                  "metadata": { "authorization": "bearer ${token}" },
                  "assert": [
                    { "path": "status", "op": "eq", "expected": "OK" },
                    { "path": "response.id", "op": "exists" }
                  ]
                }
              ]
            }
            """;
        var c = JsonSerializer.Deserialize<TestCollection>(json, JsonOptions);
        Assert.NotNull(c);
        Assert.Equal("regression", c!.Name);
        Assert.Equal("grpc", c.Protocol);
        Assert.NotNull(c.Environment);
        Assert.Equal("abc", c.Environment!["token"]);
        var test = Assert.Single(c.Tests);
        Assert.Equal("Login", test.Name);
        Assert.Equal("users.UserService", test.Service);
        Assert.NotNull(test.Messages);
        Assert.Single(test.Messages!);
        Assert.NotNull(test.Metadata);
        Assert.Equal("bearer ${token}", test.Metadata!["authorization"]);
        Assert.NotNull(test.Assert);
        Assert.Equal(2, test.Assert!.Count);
        Assert.Equal("status", test.Assert![0].Path);
        Assert.Equal("eq", test.Assert![0].Op);
        Assert.Equal("OK", test.Assert![0].Expected);
        Assert.Equal("exists", test.Assert![1].Op);
    }
}
