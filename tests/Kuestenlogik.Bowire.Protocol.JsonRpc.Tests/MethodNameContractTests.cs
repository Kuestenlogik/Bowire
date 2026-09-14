// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.JsonRpc;

namespace Kuestenlogik.Bowire.Protocol.JsonRpc.Tests;

/// <summary>
/// #664 — JSON-RPC names a method <c>Methods/&lt;name&gt;</c> in full and
/// invokes it by the bare name. Both forms arrive as the name.
/// </summary>
public sealed class MethodNameContractTests
{
    [Theory]
    [InlineData("Methods", "add", "add")]
    [InlineData("Methods", "Methods/add", "add")]
    [InlineData("Methods", "Methods/", "Methods/")]
    public void Name_And_FullName_Both_Resolve_To_The_Name(string service, string sent, string expected)
    {
        using var owned = new BowireJsonRpcProtocol();
        IBowireProtocol p = owned;
        Assert.Equal(expected, p.ResolveMethodName(service, sent));
    }
}
