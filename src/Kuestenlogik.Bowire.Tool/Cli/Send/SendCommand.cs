// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.App.Cli.Send;

/// <summary>
/// <c>bowire send &lt;protocol&gt; …</c> (#678): send a request the operator
/// spells out, directly, with no discovery and no schema. The counterpart of
/// <c>call</c>, which invokes a discovered method by name. The protocol comes
/// second and decides the arguments; <c>http</c> is the first.
/// </summary>
internal static class SendCommand
{
    /// <summary>The options every protocol of <c>send</c> shares, defined once.</summary>
    internal sealed record SharedOptions(
        Option<string[]> Vars, Option<string[]> VarFiles, Option<bool> Compact, Option<bool> Offline)
    {
        public static SharedOptions Create(IConfiguration cfg) => new(
            new Option<string[]>("--var", "--env")
            {
                Description = BowireLocale.T("cli.send.var"),
                AllowMultipleArgumentsPerToken = false,
            },
            new Option<string[]>("--env-file")
            {
                Description = BowireLocale.T("cli.send.env-file"),
                AllowMultipleArgumentsPerToken = false,
            },
            new Option<bool>("--compact")
            {
                Description = BowireLocale.T("cli.send.compact"),
                DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Cli:Compact"),
            },
            new Option<bool>("--offline")
            {
                Description = BowireLocale.T("cli.send.offline"),
            });

        public void AddTo(Command command)
        {
            command.Add(Vars);
            command.Add(VarFiles);
            command.Add(Compact);
            command.Add(Offline);
        }
    }

    /// <summary>The <c>send</c> group with its protocols.</summary>
    public static Command Build(IConfiguration cfg)
    {
        var send = new Command("send",
            BowireLocale.T("cli.send.send.command"));
        send.Add(BuildHttp(cfg));
        return send;
    }

    /// <summary><c>bowire send http [METHOD] &lt;url&gt; [items…]</c>.</summary>
    internal static Command BuildHttp(IConfiguration cfg)
    {
        var shared = SharedOptions.Create(cfg);
        var request = new Argument<string[]>("request")
        {
            Description = BowireLocale.T("cli.send.request"),
            Arity = ArgumentArity.OneOrMore,
        };
        var data = new Option<string?>("-d", "--data")
        {
            Description = BowireLocale.T("cli.send.d"),
            HelpName = "body|@file",
        };
        var include = new Option<bool>("--include", "-i")
        {
            Description = BowireLocale.T("cli.send.include"),
        };
        var output = new Option<string?>("--output", "-o")
        {
            Description = BowireLocale.T("cli.send.output"),
            HelpName = "file|-",
        };
        var negotiate = new Option<bool>("--negotiate")
        {
            Description = BowireLocale.T("cli.send.negotiate"),
        };
        var ntlm = new Option<string?>("--ntlm")
        {
            Description = BowireLocale.T("cli.send.ntlm"),
        };
        var digest = new Option<string?>("--digest")
        {
            Description = BowireLocale.T("cli.send.digest"),
        };

        var cmd = new Command("http",
            BowireLocale.T("cli.send.http.command"));
        cmd.Add(request);
        cmd.Add(data);
        cmd.Add(include);
        cmd.Add(output);
        shared.AddTo(cmd);
        cmd.Add(negotiate); cmd.Add(ntlm); cmd.Add(digest);
        cmd.SetAction(async (pr, ct) =>
        {
            var options = new HttpSendOptions
            {
                Tokens = pr.GetValue(request) ?? [],
                Data = pr.GetValue(data),
                Vars = pr.GetValue(shared.Vars) ?? [],
                VarFiles = pr.GetValue(shared.VarFiles) ?? [],
                Compact = pr.GetValue(shared.Compact),
                Offline = pr.GetValue(shared.Offline),
                Negotiate = pr.GetValue(negotiate),
                Ntlm = pr.GetValue(ntlm),
                Digest = pr.GetValue(digest),
                Include = pr.GetValue(include),
                Output = pr.GetValue(output),
                DefaultScheme = cfg["Bowire:Cli:Send:DefaultScheme"] ?? "http",
            };
            return await HttpSender.RunAsync(options,
                new CommandIo(pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error),
                () => Kuestenlogik.Bowire.Net.BowireHttpClientFactory.Create(cfg, "rest"),
                ct).ConfigureAwait(false);
        });
        return cmd;
    }
}
