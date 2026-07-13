// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// A configurable <see cref="IBowireProtocol"/> for the executor + end-to-end
/// tests — its <see cref="InvokeAsync"/> returns a canned result or throws, so
/// the recording-replay path runs without a live target.
/// </summary>
internal sealed class StubProtocol(string id, Func<InvokeResult>? invoke = null, bool throws = false) : IBowireProtocol
{
    public string Id { get; } = id;
    public string Name => "Stub";
    public string IconSvg => "<svg/>";

    public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
        => Task.FromResult(new List<BowireServiceInfo>());

    public Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method, List<string> jsonMessages,
        bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        if (throws) throw new InvalidOperationException("connection refused");
        return Task.FromResult(invoke?.Invoke() ?? new InvokeResult("""{"ok":true}""", 10, "OK", new Dictionary<string, string>()));
    }

#pragma warning disable CS1998
    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method, List<string> jsonMessages,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    { yield break; }
#pragma warning restore CS1998

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(null);
}
