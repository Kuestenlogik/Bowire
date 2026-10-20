// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.App.Configuration;

/// <summary>
/// Provider-namespaced auth flags (#818): <c>--auth-&lt;provider&gt;-&lt;setting&gt; value</c>
/// becomes <c>Bowire:Auth:&lt;Provider&gt;:&lt;Setting&gt;</c>, so an auth plugin's
/// settings can be given on the command line without the tool knowing the
/// plugin — <c>--auth-oidc-client-id bowire</c> is
/// <c>Bowire:Auth:Oidc:ClientId</c>.
/// </summary>
/// <remarks>
/// <para>
/// A value shaped <c>name=value</c> is an entry of a map setting:
/// <c>--auth-oidc-required-claim groups=bowire-users</c> is
/// <c>Bowire:Auth:Oidc:RequiredClaim:groups = bowire-users</c>, and the flag
/// may repeat. The name has to look like a claim or key (letters, digits,
/// <c>_ . : -</c>, no <c>/</c>), so a URL such as an authority is never
/// split at an <c>=</c> in its query.
/// </para>
/// <para>
/// These flags cannot be declared up front — the provider is a plugin — so
/// they are taken out of the argument list before the command-line parser
/// sees it, and handed to the configuration as settings instead. Both the
/// bootstrap configuration and the web host's read them from here, which is
/// what puts them where the provider looks: the host's configuration.
/// </para>
/// </remarks>
internal static partial class AuthProviderFlags
{
    private const string Prefix = "--auth-";

    /// <summary>The flag that picks the provider; it has a declared option of its own.</summary>
    public const string ProviderFlag = "--auth-provider";

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.:\-]*=")]
    private static partial Regex MapEntry();

    /// <summary>
    /// Split <paramref name="args"/> into what the parser should see and the
    /// configuration entries the auth flags stand for.
    /// </summary>
    public static (string[] Remaining, Dictionary<string, string?> Settings) Extract(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var remaining = new List<string>(args.Length);
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!IsProviderSetting(token, out var flag, out var inlineValue))
            {
                remaining.Add(token);
                continue;
            }

            string? value = inlineValue;
            if (value is null && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                value = args[++i];
            }

            var key = ConfigKey(flag);
            if (key is null)
            {
                // `--auth-oidc` with no setting name: not ours to guess.
                // Leave it for the parser, which will say it is unknown.
                remaining.Add(token);
                if (value is not null && inlineValue is null) remaining.Add(value);
                continue;
            }

            if (value is not null && MapEntry().IsMatch(value))
            {
                var split = value.IndexOf('=', StringComparison.Ordinal);
                settings[key + ":" + value[..split]] = value[(split + 1)..];
            }
            else
            {
                // A bare flag is a switch, as elsewhere in the CLI.
                settings[key] = value ?? "true";
            }
        }

        return ([.. remaining], settings);
    }

    private static bool IsProviderSetting(string token, out string flag, out string? inlineValue)
    {
        flag = token;
        inlineValue = null;
        if (!token.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var eq = token.IndexOf('=', StringComparison.Ordinal);
        if (eq > 0)
        {
            flag = token[..eq];
            inlineValue = token[(eq + 1)..];
        }
        return !string.Equals(flag, ProviderFlag, StringComparison.Ordinal);
    }

    /// <summary><c>--auth-oidc-client-id</c> → <c>Bowire:Auth:Oidc:ClientId</c>; <see langword="null"/> without a setting name.</summary>
    private static string? ConfigKey(string flag)
    {
        var rest = flag[Prefix.Length..];
        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || dash == rest.Length - 1) return null;
        return "Bowire:Auth:" + Pascal(rest[..dash]) + ":" + Pascal(rest[(dash + 1)..]);
    }

    private static string Pascal(string kebab)
    {
        var sb = new StringBuilder(kebab.Length);
        foreach (var part in kebab.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append(char.ToUpper(part[0], CultureInfo.InvariantCulture));
            sb.Append(part.AsSpan(1));
        }
        return sb.ToString();
    }
}
