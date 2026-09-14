// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Grpc;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// #664 — gRPC names a method <c>package.Service/Method</c> in full and
/// invokes it by the bare name. Both forms arrive as the name.
/// </summary>
public sealed class MethodNameContractTests
{
    [Theory]
    [InlineData("demo.Greeter", "SayHello", "SayHello")]
    [InlineData("demo.Greeter", "demo.Greeter/SayHello", "SayHello")]
    [InlineData("demo.Greeter", "other.Greeter/SayHello", "other.Greeter/SayHello")]
    public void Name_And_FullName_Both_Resolve_To_The_Name(string service, string sent, string expected)
    {
        IBowireProtocol p = new BowireGrpcProtocol();
        Assert.Equal(expected, p.ResolveMethodName(service, sent));
    }
}
