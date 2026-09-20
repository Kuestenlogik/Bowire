// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Disk-backed store for the schemas uploaded through the workbench — a
/// <c>.proto</c>, an OpenAPI or Swagger document (#654).
/// </summary>
/// <remarks>
/// <para>
/// Both kinds used to live in a process-wide <c>static List</c>, which had
/// four consequences that each looked like a separate defect:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Gone on restart.</b> The operator re-uploaded the spec every time.
/// </description></item>
/// <item><description>
/// <b>Shared across identities.</b> One static, one process: on a multi-user
/// host, whoever uploaded the spec for an internal service had published its
/// shape to everyone else. Recordings, collections, flows and plugin settings
/// were all moved out of exactly this arrangement; the schemas were missed
/// because nothing in the UI makes them look like stored data.
/// </description></item>
/// <item><description>
/// <b>Not per workspace.</b> Two workspaces pointed at different services saw
/// one merged pile.
/// </description></item>
/// <item><description>
/// <b>Invisible to the CLI.</b> <c>bowire discover</c> and <c>bowire test</c>
/// run in their own process, so a schema the workbench held did not exist for
/// them — and nothing reported the disagreement, the terminal simply found
/// less.
/// </description></item>
/// </list>
/// <para>
/// The layout follows <see cref="ChunkedRecordingStore"/> rather than
/// <see cref="FlowStore"/>: a <c>schemas/</c> directory with one file per
/// document under its own name, and a small <c>index.json</c> carrying id,
/// origin and kind. A schema is bytes and sometimes large, where a flow or a
/// collection is small JSON — but the deciding argument is the git-native
/// workspace. The reason flows left the browser in #641 was that a team's
/// shared artifact belongs somewhere reviewable; that only pays off if the
/// schema lands as a readable <c>.proto</c> in the diff instead of a JSON
/// string with escaped newlines.
/// </para>
/// <para>
/// There is deliberately no content cache here. Reading a handful of small
/// files per discovery is cheap, and a cache would reintroduce the fourth
/// problem in miniature: the CLI writing a schema that this process keeps not
/// seeing. Callers that pay to <em>parse</em> what they read cache the parse
/// instead, keyed on the ids this returns.
/// </para>
/// </remarks>
internal static partial class SchemaUploadStore
{
    /// <summary>A <c>.proto</c> file, parsed in core by <see cref="ProtoFileParser"/>.</summary>
    internal const string ProtoKind = "proto";

    /// <summary>An OpenAPI / Swagger document, parsed by the REST plugin.</summary>
    internal const string OpenApiKind = "openapi";

    private const string SchemasDirectory = "schemas";
    private const string IndexFile = "index.json";
    private const int MaxFileNameLength = 80;

    // CodeQL cs/path-injection sanitiser barrier, the same shape
    // ChunkedRecordingStore uses: an anchored IsMatch against a restrictive
    // character class is recognised as a sanitiser, so a name that passes it
    // stops being tainted on the way into Path.Combine.
    //
    // Two sources are tainted and both funnel through here. The upload's
    // ?name= query is attacker-controlled outright. The `file` field read back
    // out of index.json is tainted too — it came from that same query once,
    // and a hand-edited index is a file anyone with the workspace can write.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeFileNamePattern();

    private static readonly Lock DiskLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>One stored document, as its callers want it.</summary>
    internal sealed record StoredSchema(string Id, string Content, string SourceName);

    /// <summary>A document the caller handed over instead of storing.</summary>
    internal sealed record ExplicitSchema(string Kind, string Content, string SourceName);

    private static readonly AsyncLocal<IReadOnlyList<ExplicitSchema>?> s_explicit = new();

    /// <summary>
    /// Serve reads for the rest of this call from <paramref name="schemas"/>
    /// rather than from disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The command line's shape, and the reason it does not want the one
    /// above. A workbench upload is stored because the person will come back
    /// to it tomorrow; a CLI run is one invocation whose inputs are named on
    /// the command line, and in CI the schema is a file in the repository
    /// already. Pointing such a run at a workspace would make it read another
    /// process's state to find something the caller could simply have named.
    /// </para>
    /// <para>
    /// Reads only. <see cref="Add"/> and <see cref="Clear"/> ignore this and
    /// go to disk, because a command that was handed its schemas has nothing
    /// to store and no business clearing what somebody else uploaded.
    /// </para>
    /// </remarks>
    internal static IDisposable EnterExplicit(IReadOnlyList<ExplicitSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        var previous = s_explicit.Value;
        s_explicit.Value = schemas;
        return new RestoreExplicit(previous);
    }

    private sealed class RestoreExplicit(IReadOnlyList<ExplicitSchema>? previous) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            s_explicit.Value = previous;
        }
    }

    private sealed record IndexEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("sourceName")] string SourceName,
        [property: JsonPropertyName("kind")] string Kind);

    private sealed record IndexDocument(
        [property: JsonPropertyName("schemas")] List<IndexEntry> Schemas);

    /// <summary>
    /// Where this call's schemas live: the workspace being served, or the
    /// identity's own slot when no workspace was named.
    /// </summary>
    /// <remarks>
    /// The workspace arrives on the ambient scope rather than as a parameter
    /// because the call that needs it most is a plugin's <c>DiscoverAsync</c>,
    /// which is handed a URL and a token and nothing else. That seam already
    /// exists for plugin settings (#640) and is entered by the endpoint filter
    /// on every Bowire route, so an upload and the discovery that reads it
    /// resolve to the same directory without either one saying so.
    /// </remarks>
    internal static string RootPath()
    {
        if (BowirePluginSettingsScope.Current is not { } scope)
            return BowireUserContext.GetUserPath(SchemasDirectory);

        return BowireUserContext.GetWorkspacePath(
            workspaceId: scope.WorkspaceId,
            storageRoot: scope.StorageRoot,
            relativePath: SchemasDirectory);
    }

    /// <summary>Store a document and return the id the caller can name it by.</summary>
    internal static string Add(string kind, string content, string? sourceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(content);

        var root = RootPath();
        var id = "upload_" + DateTime.UtcNow.Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture);

        lock (DiskLock)
        {
            var index = ReadIndex(root);
            var taken = index.Schemas.Select(e => e.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fileName = FileNameFor(kind, sourceName, id, taken);

            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, fileName), content);

            index.Schemas.Add(new IndexEntry(id, fileName, sourceName ?? "uploaded", kind));
            WriteIndex(root, index);
        }

        return id;
    }

    /// <summary>
    /// Every document of one kind: the ones the caller handed over when a
    /// scope from <see cref="EnterExplicit"/> is open, otherwise the stored
    /// ones in upload order.
    /// </summary>
    internal static IReadOnlyList<StoredSchema> GetAll(string kind)
    {
        if (s_explicit.Value is { } given)
        {
            return given
                .Where(s => string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .Select(s => new StoredSchema(ExplicitId(s), s.Content, s.SourceName))
                .ToArray();
        }

        var root = RootPath();

        lock (DiskLock)
        {
            var result = new List<StoredSchema>();
            foreach (var entry in ReadIndex(root).Schemas)
            {
                if (!string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
                if (!SafeFileNamePattern().IsMatch(entry.File)) continue;

                var path = Path.Combine(root, entry.File);
                try
                {
                    if (!File.Exists(path)) continue;
                    result.Add(new StoredSchema(entry.Id, File.ReadAllText(path), entry.SourceName));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file the index names but this process cannot read is
                    // one missing schema, not a broken workbench.
                }
            }
            return result;
        }
    }

    /// <summary>True when at least one document of this kind is stored.</summary>
    internal static bool Has(string kind) => GetAll(kind).Count > 0;

    /// <summary>
    /// Remove every document of one kind, leaving the other kind alone —
    /// "clear the uploaded protos" must not take the OpenAPI documents with
    /// it, and the two share a directory.
    /// </summary>
    internal static void Clear(string kind)
    {
        var root = RootPath();

        lock (DiskLock)
        {
            var index = ReadIndex(root);
            var keep = new List<IndexEntry>();

            foreach (var entry in index.Schemas)
            {
                if (!string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase))
                {
                    keep.Add(entry);
                    continue;
                }

                if (!SafeFileNamePattern().IsMatch(entry.File)) continue;
                try { File.Delete(Path.Combine(root, entry.File)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The index entry goes either way: an undeletable file is
                    // better forgotten than left showing in the sidebar.
                }
            }

            WriteIndex(root, new IndexDocument(keep));
        }
    }

    private static IndexDocument ReadIndex(string root)
    {
        var path = Path.Combine(root, IndexFile);
        try
        {
            if (!File.Exists(path)) return new IndexDocument([]);
            var parsed = JsonSerializer.Deserialize<IndexDocument>(File.ReadAllText(path), JsonOptions);
            return parsed?.Schemas is null ? new IndexDocument([]) : parsed;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable index reads as "nothing uploaded yet" rather than
            // an error page in front of everything else the person has.
            return new IndexDocument([]);
        }
    }

    private static void WriteIndex(string root, IndexDocument index)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, IndexFile), JsonSerializer.Serialize(index, JsonOptions));
    }

    /// <summary>
    /// A file name that keeps the operator's own name where it can, and is
    /// safe to combine with a directory in every case.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetFileName(string)"/> first, so a name of
    /// <c>../../etc/passwd</c> loses its directories before anything else
    /// looks at it; then every character outside the safe class becomes a
    /// dash. The result is checked against the pattern rather than assumed,
    /// because "I replaced the bad characters" is exactly the reasoning that
    /// produces a traversal when one case was missed.
    /// </remarks>
    private static string FileNameFor(string kind, string? sourceName, string id, HashSet<string> taken)
    {
        var raw = Path.GetFileName(sourceName ?? string.Empty);
        var cleaned = new string(raw.Select(
            c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray()).Trim('.', '-');

        if (cleaned.Length > MaxFileNameLength) cleaned = cleaned[^MaxFileNameLength..].Trim('.', '-');
        if (cleaned.Length == 0 || !SafeFileNamePattern().IsMatch(cleaned)) cleaned = "schema";
        if (!Path.HasExtension(cleaned)) cleaned += DefaultExtension(kind);

        if (taken.Contains(cleaned))
        {
            cleaned = Path.GetFileNameWithoutExtension(cleaned) + "-" + id + Path.GetExtension(cleaned);
            if (!SafeFileNamePattern().IsMatch(cleaned)) cleaned = "schema-" + id + DefaultExtension(kind);
        }

        return cleaned;
    }

    private static string DefaultExtension(string kind)
        => string.Equals(kind, ProtoKind, StringComparison.OrdinalIgnoreCase) ? ".proto" : ".json";

    /// <summary>
    /// An id for a handed-over document, derived from its name and content.
    /// </summary>
    /// <remarks>
    /// Callers cache parses against these ids (see <c>ProtoUploadStore</c>).
    /// The name alone would let a second call with the same file name but
    /// different content read the first one's parse back — a per-process CLI
    /// never sees that, but a host or a test in one process would.
    /// </remarks>
    private static string ExplicitId(ExplicitSchema schema)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(schema.SourceName + " " + schema.Content));
        // Upper-case hex: the id is compared, never lower-cased for display,
        // and CA1308 prefers the direction that cannot lose a character.
        return "schema_" + Convert.ToHexString(digest, 0, 8);
    }
}
