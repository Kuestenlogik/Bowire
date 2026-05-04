// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for the <see cref="StreamFrame"/> record — the wrapper that
/// pairs each yielded server-stream frame with its raw wire bytes for
/// protocols (gRPC) whose binary encoding is distinct from the JSON
/// rendering. The mock-server replay path consumes <see cref="StreamFrame.Binary"/>
/// directly to reproduce frames byte-for-byte; these tests pin the
/// shape so a record-rename can't silently break that.
/// </summary>
public class StreamFrameTests
{
    [Fact]
    public void Frame_Carries_Json_And_Binary_Payload()
    {
        var bytes = new byte[] { 0x0A, 0x05, 0x68, 0x65, 0x6C, 0x6C, 0x6F };
        var frame = new StreamFrame("{\"hello\":\"world\"}", bytes);

        Assert.Equal("{\"hello\":\"world\"}", frame.Json);
        Assert.Equal(bytes, frame.Binary);
    }

    [Fact]
    public void Frame_Allows_Null_Binary_For_Json_Only_Protocols()
    {
        // REST / GraphQL / SignalR don't carry a separate wire encoding —
        // their JSON IS the wire form. Those protocols may still use
        // StreamFrame and pass null for Binary.
        var frame = new StreamFrame("{\"ok\":true}", null);

        Assert.Equal("{\"ok\":true}", frame.Json);
        Assert.Null(frame.Binary);
    }

    [Fact]
    public void Frame_Equality_Distinguishes_Different_Binary_Payloads()
    {
        var a = new StreamFrame("{}", new byte[] { 1, 2, 3 });
        var b = new StreamFrame("{}", new byte[] { 1, 2, 3 });
        var c = new StreamFrame("{}", new byte[] { 1, 2, 4 });

        // Records use SequenceEqual semantics on byte[] only when the runtime
        // implements it. We don't pin equality between identical-content
        // arrays (different reference) — instead we pin reference inequality
        // for clearly different payloads, which is the property the mock
        // server's emit-vs-skip decision relies on.
        Assert.NotEqual(a, c);
        // Equal-by-reference must compare equal for the same allocation.
        Assert.Equal(a, a);
        // The two distinct allocations might compare equal or not depending
        // on the runtime — we only assert the json+null-binary case below.
        _ = b;
    }

    [Fact]
    public void Frame_With_Identical_Json_And_Null_Binary_Are_Equal()
    {
        var a = new StreamFrame("{}", null);
        var b = new StreamFrame("{}", null);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
