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
            BowireLocale.T("cli.oast.oast.command"));
        oast.Add(BuildServe());
        return oast;
    }

    private static Command BuildServe()
    {
        var serve = new Command("serve",
            BowireLocale.T("cli.oast.serve.command"));

        var domainOpt = new Option<string>("--domain") { Description = BowireLocale.T("cli.oast.domain"), Required = true };
        var publicIpOpt = new Option<string>("--public-ip") { Description = BowireLocale.T("cli.oast.public-ip"), Required = true };
        var httpPortOpt = new Option<int>("--http-port") { Description = BowireLocale.T("cli.oast.http-port") };
        var dnsPortOpt = new Option<int>("--dns-port") { Description = BowireLocale.T("cli.oast.dns-port") };
        var listenIpOpt = new Option<string>("--listen-ip") { Description = BowireLocale.T("cli.oast.listen-ip") };
        var tokenOpt = new Option<string>("--token") { Description = BowireLocale.T("cli.oast.token") };
        var idleOpt = new Option<int>("--session-idle-minutes") { Description = BowireLocale.T("cli.oast.session-idle-minutes") };

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
