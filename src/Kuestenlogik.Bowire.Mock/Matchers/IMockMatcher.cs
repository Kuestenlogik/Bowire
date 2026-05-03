// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Mock.Matchers;

/// <summary>
/// Plugs the mock server's request-matching strategy. Implementations look
/// at an incoming <see cref="MockRequest"/> and decide which recorded
/// <see cref="BowireRecordingStep"/> (if any) should answer it. Phase 1
/// ships <see cref="ExactMatcher"/>; Phase 2 adds path and topic matchers.
/// </summary>
public interface IMockMatcher
{
    /// <summary>
    /// Try to pick a recorded step that answers this request.
    /// </summary>
    /// <param name="request">Incoming wire request.</param>
    /// <param name="recording">The active recording to match against.</param>
    /// <param name="matchedStep">The chosen step, if any.</param>
    /// <returns><c>true</c> if a step matched; <c>false</c> otherwise.</returns>
    bool TryMatch(MockRequest request, BowireRecording recording, out BowireRecordingStep matchedStep);
}

/// <summary>
/// The bits of an incoming wire request the matcher can reason about.
/// Populated by <see cref="MockHandler"/> (embedded mode) or by the
/// standalone <see cref="MockServer"/> listener.
/// </summary>
public sealed class MockRequest
{
    public required string Protocol { get; init; }
    public required string HttpMethod { get; init; }
    public required string Path { get; init; }
    public string? Query { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Body { get; init; }

    /// <summary>
    /// Incoming <c>Content-Type</c> header, lower-cased. Used by matchers
    /// to distinguish gRPC (<c>application/grpc*</c>) from REST.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// <c>true</c> when the request carries an <c>application/grpc</c>-family
    /// content type. Cached on <see cref="MockRequest"/> construction so
    /// matchers don't re-parse the header on every candidate step.
    /// </summary>
    public bool IsGrpc =>
        ContentType is not null &&
        ContentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase);
}

