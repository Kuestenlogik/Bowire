// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics;

/// <summary>
/// Declaration of how a method's message-type discriminator is read off
/// the wire. Companion to <see cref="AnnotationKey.MessageType"/>: the
/// discriminator says "look here to find which shape this frame is,"
/// then the resolver looks up annotations keyed by that shape.
/// </summary>
/// <remarks>
/// <para>
/// Four flavours are supported (Phase 1 stores them, later phases read
/// them):
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <see cref="DiscriminatorKinds.WirePath"/> — a byte-offset expression
/// evaluated against the raw frame before JSON decode (e.g.
/// <c>byte[1]</c> for the DIS PDU type byte).
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="DiscriminatorKinds.JsonPath"/> — a JSONPath expression
/// evaluated after decode (e.g. <c>$.type</c> for envelope-tagged MQTT
/// / WebSocket payloads).
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="DiscriminatorKinds.Oneof"/> — name of a protobuf
/// <c>oneof</c> group.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="DiscriminatorKinds.None"/> — explicit "method is
/// single-type" marker; every frame keys under
/// <see cref="AnnotationKey.Wildcard"/>.
/// </description>
/// </item>
/// </list>
/// </remarks>
/// <param name="Kind">
/// Which flavour the <paramref name="Value"/> should be interpreted as.
/// One of the constants in <see cref="DiscriminatorKinds"/>.
/// </param>
/// <param name="Value">
/// Kind-dependent payload. For <see cref="DiscriminatorKinds.WirePath"/>
/// it is the byte-offset expression; for
/// <see cref="DiscriminatorKinds.JsonPath"/> the JSONPath; for
/// <see cref="DiscriminatorKinds.Oneof"/> the oneof group name; for
/// <see cref="DiscriminatorKinds.None"/> the empty string.
/// </param>
public sealed record Discriminator(string Kind, string Value)
{
    /// <summary>
    /// Singleton marker for "this method is single-type." Every frame
    /// resolves to <see cref="AnnotationKey.Wildcard"/>.
    /// </summary>
    public static Discriminator None { get; } = new(DiscriminatorKinds.None, string.Empty);

    /// <summary>True when this is the <see cref="DiscriminatorKinds.None"/> marker.</summary>
    public bool IsNone => string.Equals(Kind, DiscriminatorKinds.None, StringComparison.Ordinal);
}

/// <summary>The four legal values of <see cref="Discriminator.Kind"/>.</summary>
public static class DiscriminatorKinds
{
    /// <summary>Raw-bytes offset evaluated before JSON decode.</summary>
    public const string WirePath = "wirePath";

    /// <summary>JSONPath evaluated after JSON decode.</summary>
    public const string JsonPath = "jsonPath";

    /// <summary>Protobuf <c>oneof</c> group name.</summary>
    public const string Oneof = "oneof";

    /// <summary>Single-type method — no discriminator.</summary>
    public const string None = "none";
}
