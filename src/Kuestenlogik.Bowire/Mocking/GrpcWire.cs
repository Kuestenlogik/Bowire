// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Whether a recorded step is gRPC on the wire — and so replays through
/// the mock's gRPC path and needs its HTTP/2 listener — decided by what
/// the step carries, not by the label its plugin gave it.
/// </summary>
/// <remarks>
/// <para>
/// A protocol plugin built on gRPC records its steps under its own id:
/// TacticalAPI's say <c>protocol: "tacticalapi"</c>. Since the host
/// consumes <see cref="IBowireStreamingWithWireBytes"/> for every plugin
/// that implements it, those steps carry <c>responseBinary</c> on the
/// unary response and on every streamed frame — the same bytes a step
/// labelled <c>grpc</c> carries, and everything the gRPC replayer needs.
/// Until the mock read the bytes rather than the label, such a recording
/// replayed nothing: the host recorded 1:1 and the mock answered 501.
/// </para>
/// <para>
/// The label <c>grpc</c> still counts on its own, so a gRPC step
/// recorded before wire bytes were captured keeps reaching the replayer
/// and its "re-record with a current Bowire" answer.
/// </para>
/// </remarks>
public static class GrpcWire
{
    /// <summary>The step's wire is gRPC: labelled so, or carrying gRPC wire bytes and no HTTP routing.</summary>
    public static bool IsGrpcStep(BowireRecordingStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (string.Equals(step.Protocol, "grpc", StringComparison.OrdinalIgnoreCase)) return true;
        // Inferred from the bytes, the step also has to be addressable
        // on the wire — a service and a method for the path to match.
        if (string.IsNullOrEmpty(step.Service) || string.IsNullOrEmpty(step.Method)) return false;
        if (!string.IsNullOrEmpty(step.HttpPath)) return false;
        if (!string.IsNullOrEmpty(step.ResponseBinary)) return true;
        return step.ReceivedMessages is { Count: > 0 } frames
            && frames.All(f => !string.IsNullOrEmpty(f.ResponseBinary));
    }

    /// <summary>
    /// Does the wire path <c>/{package.Service}/{Method}</c> name this
    /// step? A step whose plugin recorded the fully-qualified service
    /// matches on the whole path. A plugin that discovers its services by
    /// simple name — TacticalAPI's <c>Situation</c> for
    /// <c>rheinmetall.tactical_api.v0.Situation</c> — records that name,
    /// and matches on the last segment of the wire service.
    /// </summary>
    public static bool MatchesPath(BowireRecordingStep step, string path)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(step.Service) || string.IsNullOrEmpty(step.Method)) return false;
        if (string.Equals(path, "/" + step.Service + "/" + step.Method, StringComparison.Ordinal)) return true;
        if (step.Service.Contains('.', StringComparison.Ordinal)) return false;

        // /{package.Service}/{Method}: the method is the last segment, the
        // service everything between the leading slash and it.
        var slash = path.LastIndexOf('/');
        if (slash <= 1 || path[0] != '/') return false;
        var wireService = path[1..slash];
        var wireMethod = path[(slash + 1)..];
        if (!string.Equals(wireMethod, step.Method, StringComparison.Ordinal)) return false;
        var dot = wireService.LastIndexOf('.');
        var simple = dot < 0 ? wireService : wireService[(dot + 1)..];
        return string.Equals(simple, step.Service, StringComparison.Ordinal);
    }
}
