// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Net;

/// <summary>
/// The hosts that bypass the proxy (#680), in the forms a <c>NO_PROXY</c>
/// variable or a browser's bypass list uses.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>example.com</c> — that host and every subdomain of it (curl's reading).</item>
/// <item><c>.example.com</c> / <c>*.example.com</c> — the same: the domain and its subdomains.</item>
/// <item><c>api-*.corp</c> — a glob; <c>*</c> matches any run of characters.</item>
/// <item><c>host:8443</c> — only that port.</item>
/// <item><c>10.0.0.0/8</c>, <c>192.168.1.7</c> — an IP range or address.</item>
/// <item><c>&lt;local&gt;</c> — any host name without a dot.</item>
/// <item><c>*</c> — everything; the proxy is never used.</item>
/// </list>
/// Entries are separated by commas, semicolons or whitespace; case does not matter.
/// </remarks>
public sealed class BowireNoProxyList
{
    private readonly List<Entry> _entries;

    private BowireNoProxyList(List<Entry> entries) => _entries = entries;

    /// <summary>A list that bypasses nothing.</summary>
    public static BowireNoProxyList Empty { get; } = new([]);

    /// <summary>The entries as given, normalised, for display.</summary>
    public IReadOnlyList<string> Entries => _entries.Select(e => e.Text).ToArray();

    /// <summary>Parse one or more lists; <c>null</c> and blank parts are ignored.</summary>
    public static BowireNoProxyList Parse(params string?[] lists)
    {
        var entries = new List<Entry>();
        foreach (var list in lists)
        {
            if (string.IsNullOrWhiteSpace(list)) continue;
            foreach (var raw in list.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = Entry.TryCreate(raw.Trim());
                if (entry is not null && !entries.Any(e => string.Equals(e.Text, entry.Text, StringComparison.OrdinalIgnoreCase)))
                    entries.Add(entry);
            }
        }
        return entries.Count == 0 ? Empty : new BowireNoProxyList(entries);
    }

    /// <summary>Whether a request to <paramref name="target"/> goes direct.</summary>
    public bool Matches(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_entries.Count == 0) return false;
        var host = target.IdnHost.Trim('[', ']');
        var ip = IPAddress.TryParse(host, out var parsed) ? parsed : null;
        foreach (var e in _entries)
        {
            if (e.Matches(host, ip, target.Port)) return true;
        }
        return false;
    }

    private sealed class Entry
    {
        public required string Text { get; init; }
        private bool _all;
        private bool _local;
        private int? _port;
        private IPNetwork? _network;
        private string? _domain;
        private Regex? _glob;

        public static Entry? TryCreate(string raw)
        {
            if (raw.Length == 0) return null;
            var text = raw;
            if (text == "*") return new Entry { Text = text, _all = true };
            if (string.Equals(text, "<local>", StringComparison.OrdinalIgnoreCase)) return new Entry { Text = text, _local = true };

            if (text.Contains('/', StringComparison.Ordinal) && IPNetwork.TryParse(text, out var net))
                return new Entry { Text = text, _network = net };

            var hostPart = text;
            int? port = null;
            // host:port, but not a bare IPv6 address; [v6]:port is bracketed.
            var colon = text.LastIndexOf(':');
            if (colon > 0 && text.IndexOf(':', StringComparison.Ordinal) == colon
                && int.TryParse(text[(colon + 1)..], out var p))
            {
                hostPart = text[..colon];
                port = p;
            }
            else if (text.StartsWith('[') && text.Contains("]:", StringComparison.Ordinal))
            {
                var end = text.IndexOf("]:", StringComparison.Ordinal);
                if (int.TryParse(text[(end + 2)..], out var p6)) port = p6;
                hostPart = text[1..end];
            }
            hostPart = hostPart.Trim('[', ']');

            if (IPAddress.TryParse(hostPart, out var addr))
                return new Entry { Text = text, _network = new IPNetwork(addr, addr.GetAddressBytes().Length * 8), _port = port };

            if (hostPart.StartsWith("*.", StringComparison.Ordinal)) hostPart = hostPart[2..];
            else if (hostPart.StartsWith('.')) hostPart = hostPart[1..];
            if (hostPart.Length == 0) return null;

            if (hostPart.Contains('*', StringComparison.Ordinal))
            {
                var pattern = "^" + Regex.Escape(hostPart).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
                return new Entry
                {
                    Text = text,
                    _glob = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)),
                    _port = port,
                };
            }
            return new Entry { Text = text, _domain = hostPart, _port = port };
        }

        public bool Matches(string host, IPAddress? ip, int port)
        {
            if (_all) return true;
            if (_port is { } p && p != port) return false;
            if (_local) return ip is null && !host.Contains('.', StringComparison.Ordinal);
            if (_network is { } net) return ip is not null && net.Contains(ip);
            if (_glob is not null) return _glob.IsMatch(host);
            return _domain is not null
                && (string.Equals(host, _domain, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("." + _domain, StringComparison.OrdinalIgnoreCase));
        }
    }
}
