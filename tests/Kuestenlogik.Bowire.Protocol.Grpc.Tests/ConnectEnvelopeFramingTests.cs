// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Kuestenlogik.Bowire.Protocol.Grpc;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// Unit coverage for the Connect-protocol envelope framing used by
/// <see cref="ConnectInvoker"/>'s server-streaming path
/// (Phase 2). Locks down the wire-format helpers:
/// <list type="bullet">
///   <item>5-byte header: 1 flag byte + 4 big-endian length bytes.</item>
///   <item>Round-trip through encode → read.</item>
///   <item>End-of-stream JSON parser (<c>error</c> block extraction).</item>
///   <item>Truncated header / truncated payload error paths.</item>
/// </list>
/// Live wire round-trip against a real Connect server lives in a
/// future integration test once Connect-RPC's Go reference server
/// joins the integration suite.
/// </summary>
public sealed class ConnectEnvelopeFramingTests
{
    [Fact]
    public void EncodeFrame_writes_flag_byte_then_big_endian_length_then_payload()
    {
        var payload = Encoding.UTF8.GetBytes("hello");
        var frame = ConnectInvoker.EncodeFrame(flags: 0x00, payload: payload);

        Assert.Equal(5 + payload.Length, frame.Length);
        Assert.Equal(0x00, frame[0]);
        // 4-byte big-endian length encoding "5" is 00 00 00 05
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x05 }, frame[1..5]);
        Assert.Equal(payload, frame[5..]);
    }

    [Fact]
    public void EncodeFrame_handles_zero_length_payload()
    {
        var frame = ConnectInvoker.EncodeFrame(flags: ConnectInvoker.EndStreamFlag, payload: []);
        Assert.Equal(5, frame.Length);
        Assert.Equal(ConnectInvoker.EndStreamFlag, frame[0]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, frame[1..5]);
    }

    [Fact]
    public async Task ReadOneFrameAsync_roundtrips_an_encoded_frame()
    {
        var payload = Encoding.UTF8.GetBytes("frame-body");
        var bytes = ConnectInvoker.EncodeFrame(flags: 0x00, payload: payload);
        using var stream = new MemoryStream(bytes);

        var frame = await ConnectInvoker.ReadOneFrameAsync(stream, TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.Equal(0x00, frame!.Flags);
        Assert.Equal(payload, frame.Payload);
    }

    [Fact]
    public async Task ReadOneFrameAsync_returns_null_on_clean_stream_end()
    {
        using var empty = new MemoryStream();
        var frame = await ConnectInvoker.ReadOneFrameAsync(empty, TestContext.Current.CancellationToken);
        Assert.Null(frame);
    }

    [Fact]
    public async Task ReadOneFrameAsync_reads_multiple_frames_in_sequence()
    {
        var first = ConnectInvoker.EncodeFrame(flags: 0x00, payload: Encoding.UTF8.GetBytes("one"));
        var second = ConnectInvoker.EncodeFrame(flags: 0x00, payload: Encoding.UTF8.GetBytes("two"));
        var third = ConnectInvoker.EncodeFrame(
            flags: ConnectInvoker.EndStreamFlag, payload: Encoding.UTF8.GetBytes("{}"));

        var combined = new byte[first.Length + second.Length + third.Length];
        Buffer.BlockCopy(first, 0, combined, 0, first.Length);
        Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
        Buffer.BlockCopy(third, 0, combined, first.Length + second.Length, third.Length);
        using var stream = new MemoryStream(combined);

        var f1 = await ConnectInvoker.ReadOneFrameAsync(stream, TestContext.Current.CancellationToken);
        var f2 = await ConnectInvoker.ReadOneFrameAsync(stream, TestContext.Current.CancellationToken);
        var f3 = await ConnectInvoker.ReadOneFrameAsync(stream, TestContext.Current.CancellationToken);
        var f4 = await ConnectInvoker.ReadOneFrameAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal("one", Encoding.UTF8.GetString(f1!.Payload));
        Assert.Equal("two", Encoding.UTF8.GetString(f2!.Payload));
        Assert.Equal(ConnectInvoker.EndStreamFlag, f3!.Flags);
        Assert.Null(f4);
    }

    [Fact]
    public async Task ReadOneFrameAsync_throws_on_truncated_header()
    {
        // Stream with only 3 bytes — header needs 5.
        using var stream = new MemoryStream(new byte[] { 0x00, 0x00, 0x00 });
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ConnectInvoker.ReadOneFrameAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadOneFrameAsync_throws_on_truncated_payload()
    {
        // Header announces 10 bytes of payload but stream only carries 3.
        var bytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x0A, 0x01, 0x02, 0x03 };
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ConnectInvoker.ReadOneFrameAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ParseEndOfStreamError_extracts_code_and_message()
    {
        var payload = Encoding.UTF8.GetBytes(
            """{"error":{"code":"unavailable","message":"upstream down"},"metadata":{}}""");
        var (code, msg) = ConnectInvoker.ParseEndOfStreamError(payload);
        Assert.Equal("connect:unavailable", code);
        Assert.Equal("upstream down", msg);
    }

    [Fact]
    public void ParseEndOfStreamError_returns_nulls_on_empty_payload()
    {
        var (code, msg) = ConnectInvoker.ParseEndOfStreamError([]);
        Assert.Null(code);
        Assert.Null(msg);
    }

    [Fact]
    public void ParseEndOfStreamError_returns_nulls_when_no_error_block()
    {
        // Clean stream end carries no `error` — that's how Connect signals
        // OK. Caller treats nulls as "completed normally".
        var payload = Encoding.UTF8.GetBytes("""{"metadata":{"trailer":"value"}}""");
        var (code, msg) = ConnectInvoker.ParseEndOfStreamError(payload);
        Assert.Null(code);
        Assert.Null(msg);
    }

    [Fact]
    public void ParseEndOfStreamError_returns_nulls_on_malformed_json()
    {
        var payload = Encoding.UTF8.GetBytes("{ not json");
        var (code, msg) = ConnectInvoker.ParseEndOfStreamError(payload);
        Assert.Null(code);
        Assert.Null(msg);
    }

    [Fact]
    public void EndStreamFlag_bit_is_2()
    {
        // Spec: end-of-stream is the second bit (0x02). Locked here so
        // we notice if anyone "modernises" the constant by accident.
        Assert.Equal(0x02, ConnectInvoker.EndStreamFlag);
    }
}
