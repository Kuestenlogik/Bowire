// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Testing;

/// <summary>
/// A minimal <see cref="IBowireProtocol"/> stand-in that records every
/// <see cref="InvokeAsync"/> call so test code can assert what a
/// resolver / adapter sent down to the wire-plugin layer, without
/// spinning up the real plugin (Kafka broker, NATS server, AMQP
/// connection, &amp;c).
///
/// <para>
/// Three resolver tests originally shipped this as a nested type
/// (<c>KafkaBindingResolverTests.CapturingProtocol</c>, …); they now
/// share this single copy via the <c>Kuestenlogik.Bowire.Testing</c>
/// package. Returns <c>OK</c> with empty metadata so the caller's
/// assertions can focus on the arguments captured here rather than
/// inventing a response shape.
/// </para>
/// </summary>
public sealed class CapturingBowireProtocol(string id, string? iconSvg = null) : IBowireProtocol
{
    public string Id { get; } = id;
    public string Name => "Capturing " + Id;
    public string IconSvg { get; } = iconSvg ?? "<svg/>";

    /// <summary>Server URL the last <see cref="InvokeAsync"/> call landed on.</summary>
    public string? LastServerUrl { get; private set; }

    /// <summary>Service argument from the last <see cref="InvokeAsync"/> call.</summary>
    public string? LastService { get; private set; }

    /// <summary>Method argument from the last <see cref="InvokeAsync"/> call.</summary>
    public string? LastMethod { get; private set; }

    /// <summary>JSON-message list from the last <see cref="InvokeAsync"/> call (copied).</summary>
    public List<string>? LastJsonMessages { get; private set; }

    /// <summary>Metadata bag from the last <see cref="InvokeAsync"/> call (copied).</summary>
    public Dictionary<string, string>? LastMetadata { get; private set; }

    /// <summary>How often <see cref="InvokeAsync"/> has been called.</summary>
    public int InvokeCount { get; private set; }

    public Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
        => Task.FromResult(new List<BowireServiceInfo>());

    public Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        LastServerUrl = serverUrl;
        LastService = service;
        LastMethod = method;
        // Defensive copies so the caller's local mutation after the
        // call doesn't change what the test sees on the assertion.
        LastJsonMessages = new List<string>(jsonMessages);
        LastMetadata = metadata is null ? null : new Dictionary<string, string>(metadata);
        InvokeCount++;
        return Task.FromResult(new InvokeResult(
            Response: "{}", DurationMs: 1, Status: "OK",
            Metadata: new Dictionary<string, string>()));
    }

#pragma warning disable CS1998 // empty stream returns no yields
    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield break;
    }
#pragma warning restore CS1998

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(null);
}
