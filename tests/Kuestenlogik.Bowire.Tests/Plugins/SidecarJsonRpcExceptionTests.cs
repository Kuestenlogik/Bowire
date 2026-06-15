// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins.Sidecar;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// Tests for the sidecar JSON-RPC exception shape. The class itself is
/// trivial — four ctors covering the standard <see cref="Exception"/>
/// contract plus a JSON-RPC specific
/// <c>(int code, string message, string? rawError)</c> overload — but
/// each ctor is part of the documented surface that the transport
/// rethrows through, so pin all four explicitly.
/// </summary>
public sealed class SidecarJsonRpcExceptionTests
{
    [Fact]
    public void Default_Ctor_Yields_Defaults_For_Code_And_RawError()
    {
        var ex = new SidecarJsonRpcException();

        Assert.Equal(0, ex.Code);
        Assert.Null(ex.RawError);
        Assert.Null(ex.InnerException);
        // The default Exception.Message is the framework-localised
        // "Exception of type ... was thrown." string — not null.
        Assert.NotNull(ex.Message);
    }

    [Fact]
    public void Message_Ctor_Propagates_Message_Without_Code_Or_RawError()
    {
        var ex = new SidecarJsonRpcException("transport closed");

        Assert.Equal("transport closed", ex.Message);
        Assert.Equal(0, ex.Code);
        Assert.Null(ex.RawError);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Inner_Exception_Ctor_Chains_Cause()
    {
        var inner = new InvalidOperationException("pipe broken");
        var ex = new SidecarJsonRpcException("transport failed", inner);

        Assert.Equal("transport failed", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal(0, ex.Code);
        Assert.Null(ex.RawError);
    }

    [Fact]
    public void JsonRpc_Code_Ctor_Captures_All_Three_Slots()
    {
        // Negative codes are JSON-RPC spec for protocol errors
        // (-32600 = Invalid Request, -32601 = Method Not Found, …).
        var ex = new SidecarJsonRpcException(-32601, "Method not found", "{\"code\":-32601,\"message\":\"Method not found\"}");

        Assert.Equal(-32601, ex.Code);
        Assert.Equal("Method not found", ex.Message);
        Assert.Equal("{\"code\":-32601,\"message\":\"Method not found\"}", ex.RawError);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void JsonRpc_Code_Ctor_Allows_Null_RawError()
    {
        var ex = new SidecarJsonRpcException(42, "custom error", rawError: null);

        Assert.Equal(42, ex.Code);
        Assert.Equal("custom error", ex.Message);
        Assert.Null(ex.RawError);
    }
}
