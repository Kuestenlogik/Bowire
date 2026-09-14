// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Mcp;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tests;

/// <summary>
/// #664 — MCP tools are named <c>Tools/&lt;tool&gt;</c> in full and invoked
/// by the bare tool name. Both forms arrive as the name.
/// </summary>
public sealed class MethodNameContractTests
{
    [Theory]
    [InlineData("Tools", "echo", "echo")]
    [InlineData("Tools", "Tools/echo", "echo")]
    [InlineData("Resources", "Resources/file:///a/b", "file:///a/b")]
    [InlineData("Tools", "Other/echo", "Other/echo")]
    public void Name_And_FullName_Both_Resolve_To_The_Name(string service, string sent, string expected)
    {
        IBowireProtocol p = new BowireMcpProtocol();
        Assert.Equal(expected, p.ResolveMethodName(service, sent));
    }
}
