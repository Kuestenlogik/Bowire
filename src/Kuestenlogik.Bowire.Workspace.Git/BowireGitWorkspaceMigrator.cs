// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Workspace.Git;

/// <summary>
/// Per-entity-kind migration outcome. Counts the entities written
/// to the per-entity layout, plus a flag indicating whether the
/// migration actually fired (false = nothing-to-do, kind absent on
/// disk or already migrated).
/// </summary>
public readonly record struct BowireGitWorkspaceMigrationKindReport(string EntityKind, int Migrated, bool LegacyFound)
{
    /// <summary>
    /// Convenience flag — the migration moved data on this run.
    /// Used by the CLI to surface a "nothing to do" diagnostic when
    /// every kind reports false.
    /// </summary>
    public bool DidWork => LegacyFound;
}

/// <summary>
/// Aggregate result of a single workspace migration. Carries the
/// per-kind counts so callers can render a summary, plus a flag
/// indicating whether any kind moved data on this run (useful for
/// the CLI's idempotent "nothing to do" diagnostic).
/// </summary>
public sealed class BowireGitWorkspaceMigrationReport
{
    /// <summary>Per-entity-kind migration counts, in canonical order.</summary>
    public IReadOnlyList<BowireGitWorkspaceMigrationKindReport> Kinds { get; init; }
        = Array.Empty<BowireGitWorkspaceMigrationKindReport>();

    /// <summary>True when at least one kind moved data on this run.</summary>
    public bool AnyMigrated => Kinds.Any(k => k.DidWork);

    /// <summary>Sum of per-kind migrated counts.</summary>
    public int TotalEntities => Kinds.Sum(k => k.Migrated);
}

/// <summary>
/// One-shot migration from the legacy per-user bundle layout — one
/// <c>&lt;entityKind&gt;.json</c> file per kind, each carrying an array of
/// entities — to the per-entity file layout the git-backed runtime
/// reads through (#196 Phase 2.2).
///
/// <para>
/// Used by the <c>bowire workspace migrate-format</c> CLI subcommand
/// and exposed publicly so embedded hosts can run the migration from
/// their own admin tooling. The migrator never deletes the legacy
/// bundle outright — it renames each migrated file to
/// <c>&lt;entityKind&gt;.json.legacy</c> so an operator can verify the
/// per-entity files look right before binning the original.
/// </para>
/// </summary>
public static class BowireGitWorkspaceMigrator
{
    /// <summary>
    /// The legacy bundle file shape per entity kind: a single JSON
    /// document with a top-level array of entities, each carrying at
    /// least an <c>id</c> field.
    /// </summary>
    private static readonly string[] MigratableKinds =
    [
        "environments",
        "collections",
        "recordings",
        "scripts",
        "flows",
    ];

    /// <summary>
    /// Migrate the legacy bundle layout under <paramref name="workspaceRoot"/>
    /// to the per-entity file layout. Idempotent — re-running on an
    /// already-migrated workspace is a no-op.
    /// </summary>
    /// <param name="workspaceRoot">
    /// Absolute path to a workspace directory. The same directory both
    /// the legacy <c>&lt;entityKind&gt;.json</c> bundles live under AND
    /// where the new per-entity layout will land.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A per-kind report the caller renders to stdout. Order matches
    /// <see cref="MigratableKinds"/>.
    /// </returns>
    public static async Task<BowireGitWorkspaceMigrationReport> MigrateAsync(
        string workspaceRoot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException(
                $"Workspace directory not found: {workspaceRoot}");
        }

        var store = new FileEntityStore(workspaceRoot);
        var perKind = new List<BowireGitWorkspaceMigrationKindReport>(MigratableKinds.Length);

        foreach (var kind in MigratableKinds)
        {
            ct.ThrowIfCancellationRequested();
            perKind.Add(await MigrateKindAsync(workspaceRoot, store, kind, ct).ConfigureAwait(false));
        }

        return new BowireGitWorkspaceMigrationReport { Kinds = perKind };
    }

    private static async Task<BowireGitWorkspaceMigrationKindReport> MigrateKindAsync(
        string workspaceRoot, FileEntityStore store, string entityKind, CancellationToken ct)
    {
        var legacyPath = Path.Combine(workspaceRoot, entityKind + ".json");
        if (!File.Exists(legacyPath))
        {
            // Nothing to migrate for this kind. Either the workspace was
            // freshly created via `workspace init` (in which case the
            // per-entity buckets are already correct), or this kind was
            // simply never authored in the legacy layout.
            return new BowireGitWorkspaceMigrationKindReport(entityKind, Migrated: 0, LegacyFound: false);
        }

        var raw = await File.ReadAllTextAsync(legacyPath, ct).ConfigureAwait(false);
        var migratedCount = 0;
        using (var doc = JsonDocument.Parse(raw))
        {
            migratedCount = await WriteEntriesAsync(store, entityKind, doc.RootElement, ct)
                .ConfigureAwait(false);
        }

        // Park the legacy bundle behind a .legacy extension. The
        // operator can rm it after they've eyeballed the per-entity
        // files; `git status` won't pick it up because the init
        // template's .gitignore excludes *.legacy.
        var parked = legacyPath + ".legacy";
        if (File.Exists(parked))
        {
            // Overwrite — re-running migrate-format after a manual
            // intervention shouldn't trip the operator with a "file
            // already exists" exception.
            File.Delete(parked);
        }
        File.Move(legacyPath, parked);

        return new BowireGitWorkspaceMigrationKindReport(entityKind, migratedCount, LegacyFound: true);
    }

    /// <summary>
    /// Walks the legacy bundle's root element and writes each entry to
    /// the per-entity store. Tolerant of two common legacy shapes:
    /// <list type="bullet">
    ///   <item>An array of entity objects directly at the root
    ///   (<c>[{ id: ..., ... }, ...]</c>).</item>
    ///   <item>An envelope object with a single per-kind array field
    ///   (e.g. <c>{ environments: [...] }</c> — what
    ///   <c>EnvironmentStore</c> writes).</item>
    /// </list>
    /// Anything else is reported as zero migrated; the operator can
    /// open the <c>.legacy</c> file and migrate by hand.
    /// </summary>
    private static async Task<int> WriteEntriesAsync(
        FileEntityStore store, string entityKind, JsonElement root, CancellationToken ct)
    {
        var entries = ResolveEntries(entityKind, root);
        if (entries is null)
        {
            return 0;
        }

        var written = 0;
        foreach (var entry in entries.Value.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("id", out var idProp)) continue;
            if (idProp.ValueKind != JsonValueKind.String) continue;
            var id = idProp.GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;

            var entryJson = JsonSerializer.Serialize(entry);
            await store.SaveAsync(entityKind, id, entryJson, ct).ConfigureAwait(false);
            written++;
        }
        return written;
    }

    private static JsonElement? ResolveEntries(string entityKind, JsonElement root)
    {
        // Shape 1 — bare array at the root.
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }
        // Shape 2 — envelope object with the kind name as the array
        // field. EnvironmentStore writes {globals, environments, activeEnvId};
        // the same pattern works for the other kinds when they ever
        // adopt envelopes.
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(entityKind, out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            return arr;
        }
        return null;
    }
}
