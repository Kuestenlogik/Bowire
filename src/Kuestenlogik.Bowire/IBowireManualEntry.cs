// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire;

/// <summary>
/// Optional capability of an <see cref="IBowireProtocol"/>: it describes how
/// a call to it is entered by hand, without discovery (#995).
/// </summary>
/// <remarks>
/// <para>
/// The plugin knows what addresses a call — a Kafka topic and key, a SOAP
/// action and namespace, a NATS subject and operation — and the workbench
/// renders one generic form from that description. No plugin ships workbench
/// code, so a plugin in its own repository and a sidecar can describe itself
/// the same way.
/// </para>
/// <para>
/// A call is assembled from the description: the chosen
/// <see cref="BowireManualCallKind"/> names its <c>service</c> and
/// <c>method</c> as templates over the <see cref="BowireManualField"/> values
/// (<c>{topic}</c>, <c>{subject}/request</c>), and each field placed in
/// <see cref="BowireManualFieldPlacement.Metadata"/> goes into the call's
/// metadata under its key. The call then goes through the same invoke,
/// stream or channel endpoint as any other.
/// </para>
/// </remarks>
public interface IBowireManualEntry
{
    /// <summary>
    /// The form a call to this protocol is entered with, or <c>null</c> when
    /// the plugin has none to give — a sidecar whose <c>initialize</c> reply
    /// carries no form, or one that has not been started (#1003).
    /// </summary>
    BowireManualEntryForm? DescribeManualEntry();
}

/// <summary>How a call to one protocol is entered by hand.</summary>
/// <param name="CallKinds">The kinds of call the protocol offers, in the order the form lists them. At least one.</param>
public sealed record BowireManualEntryForm(IReadOnlyList<BowireManualCallKind> CallKinds)
{
    /// <summary>The fields a call needs, in the order the form shows them.</summary>
    public IReadOnlyList<BowireManualField> Fields { get; init; } = [];

    /// <summary>An example server URL for the URL field, e.g. <c>kafka://localhost:9092</c>.</summary>
    public string? UrlPlaceholder { get; init; }

    /// <summary>
    /// A form that names only the method types a protocol offers, for a
    /// plugin whose calls the workbench already has a form of its own for
    /// (REST, gRPC, MQTT, …): the freeform form still needs to know which
    /// method types to offer.
    /// </summary>
    public static BowireManualEntryForm ForMethodTypes(params string[] methodTypes)
    {
        ArgumentNullException.ThrowIfNull(methodTypes);
        return new BowireManualEntryForm(
            [.. methodTypes.Select(t => new BowireManualCallKind(t, t, t))]);
    }
}

/// <summary>
/// One kind of call a protocol offers — Kafka's produce and consume, SignalR's
/// invoke, stream and channel.
/// </summary>
/// <param name="Id">Stable id within the protocol, e.g. <c>produce</c>.</param>
/// <param name="Label">What the form shows, e.g. <c>Produce</c>.</param>
/// <param name="MethodType">
/// How the call runs: <c>Unary</c> (invoke), <c>ServerStreaming</c> (stream),
/// <c>ClientStreaming</c> or <c>Duplex</c> (channel). See <see cref="BowireManualCallKind.MethodTypes"/>.
/// </param>
public sealed record BowireManualCallKind(string Id, string Label, string MethodType)
{
    /// <summary>The method types a call kind may name.</summary>
    public static readonly IReadOnlyList<string> MethodTypes = ["Unary", "ServerStreaming", "ClientStreaming", "Duplex"];

    /// <summary>
    /// The call's <c>service</c>, as a template: <c>{key}</c> stands for the
    /// value of the field with that key. Empty when the protocol ignores it.
    /// </summary>
    public string Service { get; init; } = "";

    /// <summary>The call's <c>method</c>, as a template like <see cref="Service"/>.</summary>
    public string Method { get; init; } = "";

    /// <summary>One sentence on what the call does.</summary>
    public string? Description { get; init; }

    /// <summary>An example payload for the body editor, in the protocol's own format.</summary>
    public string? BodyExample { get; init; }
}

/// <summary>Where a field's value goes in the call.</summary>
public enum BowireManualFieldPlacement
{
    /// <summary>Into the call's metadata, under the field's key.</summary>
    Metadata,

    /// <summary>Only into the <c>service</c> and <c>method</c> templates of the call kind.</summary>
    Template,
}

/// <summary>One field of a hand-entry form.</summary>
/// <param name="Key">The template placeholder or metadata key, e.g. <c>topic</c> or <c>soap_action</c>.</param>
/// <param name="Label">What the form shows.</param>
/// <param name="Placement">Where the value goes.</param>
public sealed record BowireManualField(string Key, string Label, BowireManualFieldPlacement Placement)
{
    /// <summary>Help text below the field.</summary>
    public string? Description { get; init; }

    /// <summary>An example value shown in the empty field.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Whether the call cannot be made without a value.</summary>
    public bool Required { get; init; }

    /// <summary><c>string</c> (default), <c>number</c>, <c>bool</c> or <c>select</c>.</summary>
    public string Type { get; init; } = "string";

    /// <summary>The choices of a <c>select</c> field.</summary>
    public IReadOnlyList<BowireManualFieldOption> Options { get; init; } = [];

    /// <summary>The value the field starts with.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// The ids of the call kinds the field belongs to; empty means every
    /// call kind. Kafka's <c>key</c> belongs to produce, not to consume.
    /// </summary>
    public IReadOnlyList<string> CallKinds { get; init; } = [];
}

/// <summary>One choice of a <c>select</c> field.</summary>
public sealed record BowireManualFieldOption(string Value, string Label);
