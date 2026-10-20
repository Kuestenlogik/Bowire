// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using DNS.Client.RequestResolver;
using DNS.Protocol;
using DNS.Protocol.ResourceRecords;
using DNS.Server;

namespace Kuestenlogik.Bowire.Oast.Server;

/// <summary>
/// The DNS half of <c>bowire oast serve</c> (#35 Phase 2f): authoritative for
/// <c>*.&lt;domain&gt;</c>, recording every lookup as a callback.
/// </summary>
/// <remarks>
/// <para>
/// A DNS query IS the evidence. When a target resolves a host we planted in a
/// probe, that lookup proves the target parsed and acted on our payload — which
/// for a blind SSRF/RCE is the whole finding, even if it never completes an
/// HTTP request.
/// </para>
/// <para>
/// Queries are answered with the server's own address rather than NXDOMAIN, on
/// purpose: a target that resolves and then *fetches* lands on the HTTP catcher
/// too, upgrading the evidence from "it looked the host up" to "it actually
/// connected". NXDOMAIN would throw that second signal away.
/// </para>
/// </remarks>
internal sealed class OastDnsCatcher : IRequestResolver
{
    private readonly string _domain;
    private readonly IPAddress _publicIp;
    private readonly OastInteractionStore _store;
    private readonly Action<string>? _log;

    // #834 — the resolver never sees who asked. DnsServer raises Requested
    // with the sender first and then hands the very same request object to
    // Resolve, so the sender is remembered per request in between. Weak keys:
    // an entry lives exactly as long as its request.
    private readonly ConditionalWeakTable<IRequest, IPEndPoint> _senders = new();

    public OastDnsCatcher(string domain, IPAddress publicIp, OastInteractionStore store, Action<string>? log = null)
    {
        _domain = domain.TrimEnd('.');
        _publicIp = publicIp;
        _store = store;
        _log = log;
    }

    /// <summary>
    /// A DNS server that answers through this catcher, wired so every recorded
    /// interaction carries the address of the resolver that asked.
    /// </summary>
    public DnsServer CreateServer()
    {
        var server = new DnsServer(this);
        server.Requested += (_, e) => _senders.AddOrUpdate(e.Request, e.Remote);
        return server;
    }

    public Task<IResponse> Resolve(IRequest request, CancellationToken cancellationToken = default)
    {
        var response = Response.FromRequest(request);
        // We are the delegated authority for this zone; say so, or resolvers
        // treat the answer as a referral.
        response.AuthorativeServer = true;
        var remoteAddress = _senders.TryGetValue(request, out var sender) ? sender.Address.ToString() : null;

        foreach (var question in request.Questions)
        {
            var name = question.Name.ToString().TrimEnd('.');
            if (!IsUnderOurDomain(name)) continue;

            var interaction = new OastInteraction
            {
                Protocol = "dns",
                FullId = name,
                UniqueId = OastInteractionStore.CorrelationIdOf(name),
                QType = question.Type.ToString(),
                RemoteAddress = remoteAddress,
                Timestamp = DateTimeOffset.UtcNow,
                RawRequest = string.Create(CultureInfo.InvariantCulture, $";; QUESTION\n;{name}. IN {question.Type}"),
            };

            if (_store.Record(name, interaction))
            {
                _log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"  [dns] {question.Type} {name}"));
            }

            // Answer A/ANY with our address so a follow-up fetch reaches the
            // HTTP catcher. Other types get an authoritative empty answer,
            // which is a valid NOERROR/NODATA rather than a lie.
            if (question.Type is RecordType.A or RecordType.ANY)
            {
                response.AnswerRecords.Add(
                    new IPAddressResourceRecord(question.Name, _publicIp, TimeSpan.FromSeconds(60)));
            }
        }

        return Task.FromResult<IResponse>(response);
    }

    /// <summary>
    /// Whether a queried name falls inside the zone we're authoritative for.
    /// Matched as a proper suffix on a label boundary — a plain
    /// <c>EndsWith(domain)</c> would also accept <c>evil-oast.example.com</c>
    /// for the domain <c>oast.example.com</c>.
    /// </summary>
    private bool IsUnderOurDomain(string name)
        => name.Equals(_domain, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("." + _domain, StringComparison.OrdinalIgnoreCase);
}
