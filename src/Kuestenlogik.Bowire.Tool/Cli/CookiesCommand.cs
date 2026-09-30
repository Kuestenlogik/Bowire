// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire cookies list | clear</c> — the cookie jars (#681) from the command
/// line, per the parity rule: what the workbench's cookie manager shows and
/// clears, a script can too.
/// </summary>
internal static class CookiesCommand
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Command Build()
    {
        var cookies = new Command("cookies",
            "Show or clear the cookie jars: the cookies each environment's calls send and keep, per workspace.");
        cookies.Add(BuildList());
        cookies.Add(BuildClear());
        return cookies;
    }

    private static Option<string?> WorkspaceOption() => new("--workspace-id")
    {
        Description = "The workspace whose jars to read (see `bowire workspace list`). Without it, the jars kept outside any workspace, as the CLI's own calls do.",
    };

    private static Command BuildList()
    {
        var list = new Command("list", "List the cookies in the jars — values masked unless --show-values.");
        var workspace = WorkspaceOption();
        var env = new Option<string?>("--env") { Description = "Only this environment's jar." };
        var showValues = new Option<bool>("--show-values") { Description = "Print cookie values. They are credentials; masked by default." };
        var json = new Option<bool>("--json") { Description = "Emit JSON." };
        list.Add(workspace); list.Add(env); list.Add(showValues); list.Add(json);
        list.SetAction(async (pr, ct) =>
        {
            var output = pr.InvocationConfiguration.Output;
            if (!TryEnter(pr.GetValue(workspace), pr.InvocationConfiguration.Error, out var scope)) return 2;
            using (scope)
            {
                var envs = pr.GetValue(env) is { Length: > 0 } one ? [one] : CookieJar.StoredEnvironments();
                var reveal = pr.GetValue(showValues);
                var jars = envs.Select(e => new
                {
                    env = e,
                    cookies = CookieJar.Snapshot(e).Select(c => new
                    {
                        c.Domain, c.Path, c.Name,
                        Value = reveal ? c.Value : "***",
                        Expires = c.Session ? null : (DateTime?)c.Expires.ToUniversalTime(),
                        c.Secure, c.HttpOnly, c.SameSite, c.Session,
                    }).ToArray(),
                }).ToArray();

                if (pr.GetValue(json))
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(jars, s_json)).ConfigureAwait(false);
                    return 0;
                }
                if (jars.All(j => j.cookies.Length == 0))
                {
                    await output.WriteLineAsync("No cookies.").ConfigureAwait(false);
                    return 0;
                }
                foreach (var jar in jars.Where(j => j.cookies.Length > 0))
                {
                    await output.WriteLineAsync($"[{jar.env}]").ConfigureAwait(false);
                    foreach (var c in jar.cookies)
                    {
                        var expiry = c.Expires is { } e ? e.ToString("u", CultureInfo.InvariantCulture) : "session";
                        var flags = string.Join(' ', new[]
                        {
                            c.Secure ? "Secure" : null,
                            c.HttpOnly ? "HttpOnly" : null,
                            c.SameSite is null ? null : "SameSite=" + c.SameSite,
                        }.Where(f => f is not null));
                        await output.WriteLineAsync($"  {c.Domain}{c.Path}  {c.Name}={c.Value}  {expiry}  {flags}".TrimEnd()).ConfigureAwait(false);
                    }
                }
                return 0;
            }
        });
        return list;
    }

    private static Command BuildClear()
    {
        var clear = new Command("clear", "Remove cookies: an environment's whole jar, or one domain in it.");
        var workspace = WorkspaceOption();
        var env = new Option<string>("--env") { Description = "The environment whose jar to clear.", Required = true };
        var domain = new Option<string?>("--domain") { Description = "Only this domain's cookies." };
        clear.Add(workspace); clear.Add(env); clear.Add(domain);
        clear.SetAction(async (pr, ct) =>
        {
            if (!TryEnter(pr.GetValue(workspace), pr.InvocationConfiguration.Error, out var scope)) return 2;
            using (scope)
            {
                var jar = CookieJar.For(pr.GetValue(env)!);
                var removed = pr.GetValue(domain) is { Length: > 0 } d ? jar.ClearDomain(d) : jar.Clear();
                await pr.InvocationConfiguration.Output.WriteLineAsync(
                    removed == 1 ? "Removed 1 cookie." : $"Removed {removed} cookies.").ConfigureAwait(false);
                return 0;
            }
        });
        return clear;
    }

    private static bool TryEnter(string? workspaceId, TextWriter error, out IDisposable? scope)
    {
        scope = null;
        if (string.IsNullOrWhiteSpace(workspaceId)) return true;
        var workspace = WorkbenchWorkspaces.All().FirstOrDefault(w => string.Equals(w.Id, workspaceId, StringComparison.Ordinal));
        if (workspace is null)
        {
            error.WriteLine($"No workspace '{workspaceId}'. `bowire workspace list` shows the ids.");
            return false;
        }
        scope = BowirePluginSettingsScope.Enter(workspace.Id, workspace.StorageRoot);
        return true;
    }
}
