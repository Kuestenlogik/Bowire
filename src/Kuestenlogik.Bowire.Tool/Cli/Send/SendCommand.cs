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
                Description = "Variable for {{name}} / ${name} in the URL and the items. KEY=VALUE; repeatable.",
                AllowMultipleArgumentsPerToken = false,
            },
            new Option<string[]>("--env-file")
            {
                Description = "File with one KEY=VALUE per line (dotenv-style) for the variables. Repeatable; --var wins.",
                AllowMultipleArgumentsPerToken = false,
            },
            new Option<bool>("--compact")
            {
                Description = "Print a JSON response on one line.",
                DefaultValueFactory = _ => cfg.GetValue<bool>("Bowire:Cli:Compact"),
            },
            new Option<bool>("--offline")
            {
                Description = "Build the request and print it, variables resolved, without sending anything.",
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
            "Send a request you spell out, directly: no discovery, no schema. "
            + "The protocol comes next and decides the arguments. Proxy and CA bundle come from "
            + "--proxy-url / --no-proxy / --ca-bundle, as for every command.");
        send.Add(BuildHttp(cfg));
        return send;
    }

    /// <summary><c>bowire send http [METHOD] &lt;url&gt; [items…]</c>.</summary>
    internal static Command BuildHttp(IConfiguration cfg)
    {
        var shared = SharedOptions.Create(cfg);
        var request = new Argument<string[]>("request")
        {
            Description = "[METHOD] URL [ITEM…]. METHOD is optional and case-insensitive: GET, or POST when "
                + "body items are given; any token works (PROPFIND). The URL may leave out the scheme "
                + "(Bowire:Cli:Send:DefaultScheme, http by default) and :3000/x means http://localhost:3000/x. "
                + "Items: Header:value, param==value (query), field=value (JSON string), field:=json (raw JSON), "
                + "field@file (multipart file upload). A backslash keeps a separator in the name: a\\=b=c.",
            Arity = ArgumentArity.OneOrMore,
        };
        var data = new Option<string?>("-d", "--data")
        {
            Description = "A raw body instead of body items, sent as application/json unless a Content-Type:… "
                + "item says otherwise. @file reads it from a file.",
            HelpName = "body|@file",
        };
        var negotiate = new Option<bool>("--negotiate")
        {
            Description = "Windows-integrated authentication (Kerberos, falling back to NTLM) as the signed-in account. Windows only.",
        };
        var ntlm = new Option<string?>("--ntlm")
        {
            Description = "NTLM with explicit credentials: [DOMAIN\\]user:password. Pass the password from a variable.",
        };
        var digest = new Option<string?>("--digest")
        {
            Description = "HTTP Digest (RFC 7616, MD5 / SHA-256, qop auth / auth-int): user:password.",
        };

        var cmd = new Command("http",
            "Send one HTTP request, URL first, in HTTPie's item notation: "
            + "`bowire send http post api.example.com/pets name=ada age:=3 Authorization:\"Bearer {{token}}\"`. "
            + "The body goes to stdout, the status line and time to stderr; exit 0 for 2xx and 3xx, 2 for 4xx "
            + "and 5xx, 1 when the request could not be sent.");
        cmd.Add(request);
        cmd.Add(data);
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
