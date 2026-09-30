// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// The cookie jars (#681): one per workspace and environment, persisted in
/// the workspace, applied to every protocol that goes over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// A call opts in by carrying the environment id under <see cref="MarkerKey"/>
/// (the workbench does so when the environment's auth has "persist cookies"
/// on). The jar is <see cref="BowireCookieJar"/>: a <see cref="CookieContainer"/>
/// for the matching — domain, path, <c>Secure</c> and expiry follow RFC 6265 —
/// plus what the container does not keep (<c>SameSite</c>) and a file.
/// </para>
/// <para>
/// Persistent cookies are written to <c>cookies.json</c> in the workspace
/// (or the user's Bowire directory when no workspace is in scope, as in the
/// CLI) and survive a restart. Session cookies stay in memory and end with
/// the process, as a browser's do.
/// </para>
/// </remarks>
public static class CookieJar
{
    /// <summary>The metadata key carrying the environment id whose jar a call uses.</summary>
    public const string MarkerKey = "__bowireCookieEnv__";

    /// <summary>Where <see cref="Attach"/> leaves the jar on a request.</summary>
    public static readonly HttpRequestOptionsKey<BowireCookieJar> OptionKey = new("Kuestenlogik.Bowire.CookieJar");

    internal const string FileName = "cookies.json";

    private static readonly ConcurrentDictionary<string, BowireCookieJar> s_jars = new(StringComparer.Ordinal);

    /// <summary>The jar for <paramref name="envId"/> in the current workspace.</summary>
    public static BowireCookieJar For(string envId)
    {
        ArgumentException.ThrowIfNullOrEmpty(envId);
        var path = StorePath();
        return s_jars.GetOrAdd((path ?? string.Empty) + "|" + envId, _ => new BowireCookieJar(envId, path));
    }

    /// <summary>The container for <paramref name="envId"/>; kept for callers that only need matching.</summary>
    public static CookieContainer GetOrCreate(string envId) => For(envId).Container;

    /// <summary>Forget every cookie of <paramref name="envId"/>, in memory and on disk.</summary>
    public static bool Clear(string envId) => For(envId).Clear() > 0;

    /// <summary>The cookies of <paramref name="envId"/> that are still valid.</summary>
    public static IReadOnlyList<CookieSnapshot> Snapshot(string envId) => For(envId).Snapshot();

    /// <summary>The environment id in <paramref name="metadata"/>, or <c>null</c>.</summary>
    public static string? EnvIdOf(IEnumerable<KeyValuePair<string, string>>? metadata)
    {
        if (metadata is null) return null;
        foreach (var (key, value) in metadata)
        {
            if (string.Equals(key, MarkerKey, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    /// <summary>Carry the call's jar, if it asks for one, on <paramref name="request"/>.</summary>
    public static void Attach(HttpRequestMessage request, IEnumerable<KeyValuePair<string, string>>? metadata)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (EnvIdOf(metadata) is { } envId) request.Options.Set(OptionKey, For(envId));
    }

    /// <summary>Every environment with a stored jar in the current workspace (for the CLI and MCP).</summary>
    public static IReadOnlyList<string> StoredEnvironments()
    {
        var path = StorePath();
        IEnumerable<string> fromFile = path is null ? [] : BowireCookieJar.ReadFile(path).Keys;
        var inMemory = s_jars.Values.Where(j => j.StorePath == path).Select(j => j.EnvId);
        return fromFile.Concat(inMemory).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The current workspace's persisted jars as JSON (<c>{ envId: [cookies] }</c>),
    /// for the workspace export. Session cookies are not in it; they end with the process.
    /// </summary>
    public static string ExportJson()
    {
        var path = StorePath();
        var stored = path is null ? new Dictionary<string, List<StoredCookie>>(StringComparer.Ordinal) : BowireCookieJar.ReadFile(path);
        return JsonSerializer.Serialize(stored, BowireCookieJar.JsonOptions);
    }

    /// <summary>
    /// Replace the current workspace's persisted jars with <paramref name="environmentsJson"/>
    /// (the shape <see cref="ExportJson"/> writes), for the workspace import.
    /// The jars held in memory for this workspace are dropped and reload.
    /// </summary>
    public static int ImportJson(string environmentsJson)
    {
        var path = StorePath() ?? throw new InvalidOperationException("No place to store cookies for this workspace.");
        var stored = JsonSerializer.Deserialize<Dictionary<string, List<StoredCookie>>>(environmentsJson, BowireCookieJar.JsonOptions)
            ?? new Dictionary<string, List<StoredCookie>>(StringComparer.Ordinal);
        BowireCookieJar.WriteFile(path, stored);
        foreach (var key in s_jars.Keys.Where(k => k.StartsWith(path + "|", StringComparison.Ordinal)).ToArray())
            s_jars.TryRemove(key, out _);
        return stored.Values.Sum(v => v.Count);
    }

    internal static string? StorePath()
    {
        try
        {
            return BowirePluginSettingsScope.Current is { } scope
                ? BowireUserContext.GetWorkspacePath(scope.WorkspaceId, scope.StorageRoot, FileName)
                : BowireUserContext.GetUserPath(FileName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // A workspace id the resolver refuses: keep the jar in memory.
            _ = ex;
            return null;
        }
    }

    /// <summary>For tests: forget every jar held in memory.</summary>
    internal static void ResetForTests() => s_jars.Clear();
}

/// <summary>One environment's cookies in one workspace (#681).</summary>
public sealed class BowireCookieJar
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Every jar of a workspace shares one file; writes to it take this lock.
    private static readonly ConcurrentDictionary<string, object> s_fileLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _sameSite = new(StringComparer.OrdinalIgnoreCase);

    internal BowireCookieJar(string envId, string? storePath)
    {
        EnvId = envId;
        StorePath = storePath;
        Load();
    }

    /// <summary>The environment id.</summary>
    public string EnvId { get; }

    /// <summary>The file persistent cookies live in, or <c>null</c> for memory only.</summary>
    public string? StorePath { get; }

    /// <summary>The container that does the matching.</summary>
    public CookieContainer Container { get; } = new();

    /// <summary>The <c>Cookie</c> header for a request to <paramref name="uri"/>.</summary>
    public string CookieHeaderFor(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        lock (_gate) return Container.GetCookieHeader(HttpTarget(uri));
    }

    /// <summary>
    /// Take the <c>Set-Cookie</c> headers of a response to <paramref name="uri"/>
    /// into the jar, and persist. A header the container rejects (wrong
    /// domain, malformed) is dropped, as a browser would.
    /// </summary>
    public void Accept(Uri uri, IEnumerable<string> setCookieHeaders)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(setCookieHeaders);
        var target = HttpTarget(uri);
        var changed = false;
        lock (_gate)
        {
            foreach (var header in setCookieHeaders)
            {
                try
                {
                    Container.SetCookies(target, header);
                    RememberSameSite(target, header);
                    changed = true;
                }
                catch (CookieException)
                {
                    // Rejected by the RFC 6265 rules — not ours to keep.
                }
            }
        }
        if (changed) Save();
    }

    /// <summary>Record what a response set after a handler stored it in <see cref="Container"/> itself.</summary>
    public void Observe(Uri uri, HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(response);
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers)) return;
        lock (_gate)
        {
            foreach (var header in headers) RememberSameSite(HttpTarget(uri), header);
        }
        Save();
    }

    /// <summary>The cookies still valid, sorted by domain, path and name.</summary>
    public IReadOnlyList<CookieSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return Container.GetAllCookies()
                .Where(c => !c.Expired && (c.Expires == DateTime.MinValue || c.Expires.ToUniversalTime() > DateTime.UtcNow))
                .Select(c => new CookieSnapshot(c.Domain, c.Path, c.Name, c.Value, c.Expires, c.Secure, c.HttpOnly)
                {
                    SameSite = _sameSite.GetValueOrDefault(Key(c.Domain, c.Path, c.Name)),
                    Session = c.Expires == DateTime.MinValue,
                })
                .OrderBy(c => c.Domain.TrimStart('.'), StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Path, StringComparer.Ordinal)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>Add a cookie or replace the one with the same domain, path and name.</summary>
    public void Set(CookieSnapshot cookie)
    {
        ArgumentNullException.ThrowIfNull(cookie);
        if (string.IsNullOrWhiteSpace(cookie.Name)) throw new ArgumentException("A cookie needs a name.", nameof(cookie));
        if (string.IsNullOrWhiteSpace(cookie.Domain)) throw new ArgumentException("A cookie needs a domain.", nameof(cookie));
        var path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path;
        lock (_gate)
        {
            RemoveLocked(cookie.Domain, path, cookie.Name);
            var added = new Cookie(cookie.Name, cookie.Value ?? string.Empty, path, cookie.Domain)
            {
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
            };
            if (!cookie.Session && cookie.Expires != DateTime.MinValue) added.Expires = cookie.Expires;
            AddLocked(added);
            if (!string.IsNullOrEmpty(cookie.SameSite)) _sameSite[Key(added.Domain, path, cookie.Name)] = cookie.SameSite;
        }
        Save();
    }

    /// <summary>Remove one cookie; <c>true</c> when it existed.</summary>
    public bool Remove(string domain, string path, string name)
    {
        bool removed;
        lock (_gate) removed = RemoveLocked(domain, path, name);
        if (removed) Save();
        return removed;
    }

    /// <summary>Remove every cookie of <paramref name="domain"/> (and its subdomains' cookies scoped to it); the count removed.</summary>
    public int ClearDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrEmpty(domain);
        var bare = domain.TrimStart('.');
        int count;
        lock (_gate)
        {
            var live = LiveLocked();
            var keep = live.Where(c => !string.Equals(c.Domain.TrimStart('.'), bare, StringComparison.OrdinalIgnoreCase)).ToList();
            count = live.Count - keep.Count;
            ReplaceLocked(keep);
        }
        if (count > 0) Save();
        return count;
    }

    /// <summary>Remove every cookie; the count removed.</summary>
    public int Clear()
    {
        int count;
        lock (_gate)
        {
            count = LiveLocked().Count;
            ReplaceLocked([]);
            _sameSite.Clear();
        }
        Save();
        return count;
    }

    /// <summary>
    /// Persist what a client library stored in <see cref="Container"/> on its
    /// own (SignalR, a WebSocket handshake), where no response passes through
    /// Bowire to <see cref="Accept"/>.
    /// </summary>
    public void Persist() => Save();

    // ---- persistence ------------------------------------------------------

    internal static Dictionary<string, List<StoredCookie>> ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            var file = JsonSerializer.Deserialize<CookieFile>(File.ReadAllText(path), JsonOptions);
            return file?.Environments ?? new(StringComparer.Ordinal);
        }
#pragma warning disable CA1031 // A broken file loses its cookies, not the workbench.
        catch
        {
            return new(StringComparer.Ordinal);
        }
#pragma warning restore CA1031
    }

    private void Load()
    {
        if (StorePath is null) return;
        if (!ReadFile(StorePath).TryGetValue(EnvId, out var stored)) return;
        foreach (var s in stored)
        {
            if (s.Expires <= DateTime.UtcNow) continue;
            try
            {
                var cookie = new Cookie(s.Name, s.Value, s.Path, s.Domain)
                {
                    Secure = s.Secure,
                    HttpOnly = s.HttpOnly,
                    Expires = s.Expires.ToLocalTime(),
                };
                AddLocked(cookie);
                if (s.SameSite is not null) _sameSite[Key(cookie.Domain, cookie.Path, cookie.Name)] = s.SameSite;
            }
            catch (CookieException)
            {
                // Edited into something invalid by hand; skip it.
            }
        }
    }

    private void Save()
    {
        if (StorePath is null) return;
        List<StoredCookie> persistent;
        lock (_gate)
        {
            persistent = Container.GetAllCookies()
                .Where(c => c.Expires != DateTime.MinValue && !c.Expired && c.Expires.ToUniversalTime() > DateTime.UtcNow)
                .Select(c => new StoredCookie
                {
                    Name = c.Name,
                    Value = c.Value,
                    Domain = c.Domain,
                    Path = c.Path,
                    Expires = c.Expires.ToUniversalTime(),
                    Secure = c.Secure,
                    HttpOnly = c.HttpOnly,
                    SameSite = _sameSite.GetValueOrDefault(Key(c.Domain, c.Path, c.Name)),
                })
                .ToList();
        }

        var fileLock = s_fileLocks.GetOrAdd(StorePath, _ => new object());
        lock (fileLock)
        {
            var all = ReadFile(StorePath);
            if (persistent.Count == 0) all.Remove(EnvId);
            else all[EnvId] = persistent;
            WriteFileLocked(StorePath, all);
        }
    }

    internal static void WriteFile(string path, Dictionary<string, List<StoredCookie>> all)
    {
        lock (s_fileLocks.GetOrAdd(path, _ => new object())) WriteFileLocked(path, all);
    }

    private static void WriteFileLocked(string path, Dictionary<string, List<StoredCookie>> all)
    {
        try
        {
            if (all.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(new CookieFile { Environments = all }, JsonOptions));
        }
        catch (IOException)
        {
            // The jar stays right in memory; the next change writes again.
        }
    }

    // ---- helpers ----------------------------------------------------------

    private bool RemoveLocked(string domain, string path, string name)
    {
        var all = LiveLocked();
        var keep = all.Where(c => !(string.Equals(c.Domain.TrimStart('.'), domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(c.Path, path, StringComparison.Ordinal)
                                    && string.Equals(c.Name, name, StringComparison.Ordinal))).ToList();
        if (keep.Count == all.Count) return false;
        ReplaceLocked(keep);
        _sameSite.Remove(Key(domain, path, name));
        return true;
    }

    // The container still counts cookies that expired but were not yet purged.
    private List<Cookie> LiveLocked() =>
        Container.GetAllCookies()
            .Where(c => !c.Expired && (c.Expires == DateTime.MinValue || c.Expires.ToUniversalTime() > DateTime.UtcNow))
            .ToList();

    private void ReplaceLocked(List<Cookie> keep)
    {
        // Copies first: expiring a cookie below sets its Expires to now, and
        // `keep` holds the container's own instances.
        var copies = keep.Select(c =>
        {
            var copy = new Cookie(c.Name, c.Value, c.Path, c.Domain) { Secure = c.Secure, HttpOnly = c.HttpOnly };
            if (c.Expires != DateTime.MinValue) copy.Expires = c.Expires;
            return copy;
        }).ToList();
        // CookieContainer has no remove; expire everything, then put back
        // what stays. An expired cookie is dropped by the container.
        foreach (Cookie c in Container.GetAllCookies()) c.Expired = true;
        foreach (var copy in copies) AddLocked(copy);
    }

    private void RememberSameSite(Uri target, string header)
    {
        // CookieContainer keeps no SameSite; read it (and where the cookie
        // landed) from the header itself.
        var parts = header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var eq = parts[0].IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0) return;
        var name = parts[0][..eq].Trim();
        string? sameSite = null;
        var domain = target.Host;
        var path = DefaultPath(target);
        foreach (var attribute in parts.Skip(1))
        {
            var ai = attribute.IndexOf('=', StringComparison.Ordinal);
            var key = ai < 0 ? attribute : attribute[..ai];
            var value = ai < 0 ? string.Empty : attribute[(ai + 1)..].Trim();
            if (key.Equals("SameSite", StringComparison.OrdinalIgnoreCase)) sameSite = value;
            else if (key.Equals("Domain", StringComparison.OrdinalIgnoreCase) && value.Length > 0) domain = value;
            else if (key.Equals("Path", StringComparison.OrdinalIgnoreCase) && value.StartsWith('/')) path = value;
        }
        var cookieKey = Key(domain, path, name);
        if (sameSite is null) _sameSite.Remove(cookieKey);
        else _sameSite[cookieKey] = sameSite;
    }

    /// <summary>
    /// Add through a URI made from the cookie's own domain: the URI-less
    /// overload rejects a host-only cookie of an IP address or a dotless
    /// host (localhost), which is exactly what a local dev server sets.
    /// </summary>
    private void AddLocked(Cookie cookie)
    {
        var host = cookie.Domain.TrimStart('.');
        var origin = new UriBuilder(cookie.Secure ? Uri.UriSchemeHttps : Uri.UriSchemeHttp, host)
        {
            Path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path,
        }.Uri;
        Container.Add(origin, cookie);
    }

    private static string DefaultPath(Uri uri)
    {
        var p = uri.AbsolutePath;
        var slash = p.LastIndexOf('/');
        return slash <= 0 ? "/" : p[..slash];
    }

    private static string Key(string domain, string path, string name) => $"{domain.TrimStart('.')}|{path}|{name}";

    // Cookies are an HTTP notion; a ws:// / wss:// handshake is an HTTP request
    // to the same origin, so it shares the http:// / https:// cookies.
    private static Uri HttpTarget(Uri uri) => uri.Scheme switch
    {
        "ws" => new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp, Port = uri.Port }.Uri,
        "wss" => new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = uri.Port }.Uri,
        _ => uri,
    };

    private sealed class CookieFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, List<StoredCookie>> Environments { get; set; } = new(StringComparer.Ordinal);
    }
}

/// <summary>A cookie as <c>cookies.json</c> stores it.</summary>
internal sealed class StoredCookie
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Path { get; set; } = "/";
    public DateTime Expires { get; set; }
    public bool Secure { get; set; }
    public bool HttpOnly { get; set; }
    public string? SameSite { get; set; }
}

/// <summary>One cookie as the manager shows it.</summary>
public sealed record CookieSnapshot(
    string Domain,
    string Path,
    string Name,
    string Value,
    DateTime Expires,
    bool Secure,
    bool HttpOnly)
{
    /// <summary><c>Strict</c>, <c>Lax</c> or <c>None</c>, as the server set it; <c>null</c> when it did not.</summary>
    public string? SameSite { get; init; }

    /// <summary>Whether this is a session cookie (no expiry), kept only in memory.</summary>
    public bool Session { get; init; }
}
