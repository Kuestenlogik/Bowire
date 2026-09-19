// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire;

/// <summary>
/// Optional extension for <see cref="IBowireProtocol"/> implementations that
/// can expose the raw wire bytes of each server-streamed frame alongside the
/// JSON rendering. Implemented by protocol plugins whose wire format is
/// binary and distinct from their JSON representation (gRPC today); the
/// JSON-only <see cref="IBowireProtocol.InvokeStreamAsync"/> stays the
/// default path for everyone else.
/// </summary>
/// <remarks>
/// The mock server's Phase-2d gRPC-streaming replay consumes the binary
/// payload so it can emit recorded frames 1:1 on the wire without runtime
/// re-encoding (Google.Protobuf's C# library has no <c>DynamicMessage</c>
/// equivalent, so reconstructing the bytes from JSON + descriptor isn't
/// possible).
/// </remarks>
public interface IBowireStreamingWithWireBytes
{
    /// <summary>
    /// Same semantics as <see cref="IBowireProtocol.InvokeStreamAsync"/>,
    /// but each yielded frame carries both the JSON payload and the raw
    /// wire bytes.
    /// </summary>
    IAsyncEnumerable<StreamFrame> InvokeStreamWithFramesAsync(
        string serverUrl,
        string service,
        string method,
        List<string> jsonMessages,
        bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        CancellationToken ct = default);
}

#pragma warning disable CA1819 // Binary is raw wire bytes — defensive copies
                              // on every access would double memory use.

/// <summary>
/// One frame yielded by a server-streaming call that exposes wire bytes.
/// <paramref name="Json"/> is the display / recording form; <paramref name="Binary"/>
/// is what the mock server replays on the wire.
/// </summary>
public sealed record StreamFrame(string Json, byte[]? Binary)
{
    /// <summary>
    /// Why the stream stopped, when it stopped for a reason (#712).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null on an ordinary frame, which is every frame carrying data. Set,
    /// it means this is the last one and <see cref="Json"/> is not payload
    /// the caller asked for.
    /// </para>
    /// <para>
    /// Before this there was nowhere to say it, so plugins wrote the reason
    /// into <see cref="Json"/> — where the consumer expects payload. The
    /// gRPC plugin had already grown a private <c>ErrorCode</c> on its own
    /// internal frame record for exactly this; that is the field this one
    /// replaces.
    /// </para>
    /// </remarks>
    public StreamError? Error { get; init; }
}

#pragma warning restore CA1819
