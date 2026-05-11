// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics;

/// <summary>
/// Four-dimensional address of a schema-field annotation:
/// <c>(service, method, message-type, json-path)</c>.
/// </summary>
/// <remarks>
/// <para>
/// The frame-semantics framework treats every annotation as a tag on a
/// specific field path inside a specific message type inside a specific
/// method on a specific service. Three dimensions are not enough — a
/// single transport channel (DIS multicast, MQTT topic, SignalR hub,
/// protobuf <c>oneof</c>, WebSocket envelope) routinely carries multiple
/// payload shapes, and the same JSON path can mean different things in
/// each. The discriminator dimension keeps those interpretations
/// separate.
/// </para>
/// <para>
/// For single-type methods (a classical REST <c>GET</c>, a unary gRPC
/// call, …) <see cref="MessageType"/> is the literal wildcard
/// <see cref="Wildcard"/> (<c>"*"</c>). The wildcard is a regular value,
/// not a separate case in the resolver — annotations under <c>"*"</c>
/// match by equality just like annotations under <c>"EntityStatePdu"</c>.
/// </para>
/// </remarks>
/// <param name="ServiceId">
/// Plugin-defined service identifier — the OpenAPI tag, gRPC service
/// FQN, SignalR hub name, MQTT topic prefix, … . Always non-null and
/// case-sensitive: two services that differ only in casing are not the
/// same service.
/// </param>
/// <param name="MethodId">
/// Plugin-defined method identifier within <paramref name="ServiceId"/>
/// — the operationId, gRPC method name, hub-method name, … . Always
/// non-null and case-sensitive.
/// </param>
/// <param name="MessageType">
/// Discriminator value selecting which shape of message this annotation
/// applies to. <see cref="Wildcard"/> (<c>"*"</c>) for single-type
/// methods; a concrete string for multi-type channels (e.g.
/// <c>"EntityStatePdu"</c>, <c>"PortCallScheduled"</c>). Case-sensitive.
/// </param>
/// <param name="JsonPath">
/// JSONPath expression rooted at the message body (<c>$</c>). The
/// resolver does an exact-string match, so <c>$.position.lat</c> and
/// <c>$.position.lat </c> (with trailing whitespace) are different
/// keys; callers should canonicalise before constructing.
/// </param>
public sealed record AnnotationKey(
    string ServiceId,
    string MethodId,
    string MessageType,
    string JsonPath)
{
    /// <summary>
    /// The literal <c>"*"</c> used for the message-type slot when a
    /// method carries a single payload shape (no discriminator).
    /// </summary>
    public const string Wildcard = "*";

    /// <summary>
    /// Convenience factory for single-type methods — fills the
    /// <see cref="MessageType"/> slot with <see cref="Wildcard"/>.
    /// </summary>
    public static AnnotationKey ForSingleType(string serviceId, string methodId, string jsonPath)
        => new(serviceId, methodId, Wildcard, jsonPath);
}
