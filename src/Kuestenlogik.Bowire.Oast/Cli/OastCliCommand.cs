// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.Cli;
using Kuestenlogik.Bowire.Oast.Server;

namespace Kuestenlogik.Bowire.Oast.Cli;

/// <summary>
/// Discoverable CLI contribution for <c>bowire oast</c> (#35 Phase 2f) — run
/// the out-of-band interaction server the scanner's <c>--oast-server</c> points
/// at. Picked up by the Tool's assembly scan; zero-config ctor by contract.
/// </summary>
public sealed class OastCliCommand : IBowireCliCommand
{
    public string Id => "oast";

    public Command Build()
    {
        var oast = new Command("oast",
            "Out-of-band interaction server (OAST) — the callback catcher that proves blind SSRF / RCE / XXE. See docs/architecture/security-testing.md.");
        oast.Add(BuildServe());
        return oast;
    }

    private static Command BuildServe()
    {
        var serve = new Command("serve",
            "Run the interaction server: a DNS + HTTP catcher for *.<domain> plus the register/poll API. Wire-compatible with interactsh, so `bowire scan --oast-server` (or any interactsh client) can point at it. Requires the domain to be NS-delegated to this host — see docs/architecture/security-testing.md.");

        var domainOpt = new Option<string>("--domain") { Description = "The delegated zone this instance is authoritative for, e.g. oast.example.com. Callback hosts are handed out beneath it. The zone must be NS-delegated to this host or nothing will ever reach the DNS catcher.", Required = true };
        var publicIpOpt = new Option<string>("--public-ip") { Description = "This host's public IP. Answered for A queries under --domain, so a target that resolves a callback host and then connects also lands on the HTTP catcher (upgrading the evidence from 'looked it up' to 'actually connected').", Required = true };
        var httpPortOpt = new Option<int>("--http-port") { Description = "HTTP catcher + API port. Default 80." };
        var dnsPortOpt = new Option<int>("--dns-port") { Description = "DNS catcher port. Default 53. A real delegation cannot use another port — override only for local testing." };
        var listenIpOpt = new Option<string>("--listen-ip") { Description = "Address to bind. Default 0.0.0.0 (all interfaces)." };
        var tokenOpt = new Option<string>("--token") { Description = "Require this value as the Authorization header on /register. Without it the instance is an OPEN callback catcher: anyone who finds it can register and point it at third-party targets." };
        var idleOpt = new Option<int>("--session-idle-minutes") { Description = "Evict sessions that stop polling after this long. Default 60. Keeps a long-lived catcher from holding other people's callback traffic indefinitely." };

        serve.Add(domainOpt);
        serve.Add(publicIpOpt);
        serve.Add(httpPortOpt);
        serve.Add(dnsPortOpt);
        serve.Add(listenIpOpt);
        serve.Add(tokenOpt);
        serve.Add(idleOpt);

        serve.SetAction(async (pr, ct) =>
        {
            var options = new OastServeOptions
            {
                Domain = pr.GetValue(domainOpt) ?? "",
                PublicIp = pr.GetValue(publicIpOpt) ?? "",
                HttpPort = pr.GetValue(httpPortOpt) is int h and > 0 ? h : 80,
                DnsPort = pr.GetValue(dnsPortOpt) is int d and > 0 ? d : 53,
                ListenIp = pr.GetValue(listenIpOpt) is { Length: > 0 } l ? l : "0.0.0.0",
                Token = pr.GetValue(tokenOpt),
                SessionIdleMinutes = pr.GetValue(idleOpt) is int m and > 0 ? m : 60,
            };
            return await BowireOastServer.RunAsync(
                options, ct, pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error).ConfigureAwait(false);
        });

        return serve;
    }
}
