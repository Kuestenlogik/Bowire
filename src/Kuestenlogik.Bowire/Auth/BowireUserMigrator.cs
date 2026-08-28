// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// Moves a single-user install's state into an identity's slot when the
/// install becomes multi-tenant (#97, #28 Phase E).
/// </summary>
/// <remarks>
/// <para>
/// The day an operator turns on an identity provider, everything already on
/// disk becomes invisible: the stores stop resolving to the flat storage root
/// and start resolving under <c>users/&lt;slot&gt;/</c>, so the newly signed-in
/// person sees an empty workbench and their collections appear to be gone.
/// They are not gone, but nothing in the product says so, and the obvious
/// conclusion — that turning on auth cost them their work — is the one people
/// draw.
/// </para>
/// <para>
/// <b>Copy, never move.</b> The legacy files stay exactly where they are. That
/// costs disk and buys two things worth more than the disk: the install can be
/// switched back to single-user without a second migration, and a migration
/// that lands the data in the wrong slot is recoverable by declining it in the
/// right one. The operator deletes the originals when they are satisfied,
/// which is a decision only they can time.
/// </para>
/// <para>
/// <b>Excluding, not including.</b> The set below names what is <em>not</em> a
/// person's state; everything else is copied. An inclusion list would have to
/// be extended by every future store, and the failure mode of forgetting is
/// silent data loss for whoever used that feature. Forgetting to exclude
/// something merely copies a cache.
/// </para>
/// </remarks>
public static class BowireUserMigrator
{
    /// <summary>The decision record, written inside the slot it concerns.</summary>
    public const string ReceiptFileName = ".migration.json";

    private const string StagingPrefix = ".staging-";

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private static readonly EnumerationOptions s_walk = new()
    {
        RecurseSubdirectories = true,
        // Not through a symlink: a reparse point in the storage root would let
        // the walk leave it, and a migration is the last place that should be
        // reachable by planting a link.
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    /// <summary>
    /// Top-level names under the storage root that belong to the machine or the
    /// install rather than to a person, and are therefore not migrated.
    /// </summary>
    public static IReadOnlySet<string> NotPersonalState { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // The slots themselves. Copying these into a slot would nest the
            // whole tenancy inside one identity, once per migration.
            BowireUserSlot.DirectoryName,

            // Installed plugins: two tiers with their own precedence rule
            // (#28 Phase D), and an admin's business rather than a person's.
            "plugins",

            // Machine-shaped or regenerable.
            "certs", "logs", "cache", "state",

            // The project manifest is read from the root and describes the
            // checkout, not the person.
            "project.json",
        };

    /// <summary>
    /// What migrating into <paramref name="subject"/>'s slot would do. Reads
    /// the disk; changes nothing.
    /// </summary>
    /// <param name="storageRoot">The data root holding the legacy flat layout.</param>
    /// <param name="subject">The authenticated subject.</param>
    /// <param name="mode">What the install has said it wants.</param>
    public static BowireUserMigrationPlan Plan(
        string storageRoot,
        string subject,
        BowireUserMigrationMode mode = BowireUserMigrationMode.Prompt)
    {
        var store = new ScopedBowireUserStore(storageRoot, subject);

        BowireUserMigrationPlan Verdict(
            BowireUserMigrationState state,
            IReadOnlyList<BowireUserMigrationEntry>? entries = null,
            BowireUserMigrationReceipt? receipt = null)
            => new()
            {
                Subject = store.Subject,
                Slug = store.Slug,
                StorageRoot = store.StorageRoot,
                Slot = store.Slot,
                State = state,
                Entries = entries ?? [],
                Receipt = receipt,
            };

        // The receipt first: it is the truth about this identity, and an
        // install that flipped to Skip after someone migrated should still be
        // able to say what happened rather than reporting "disabled".
        var decided = ReadReceipt(store.Slot);
        if (decided is not null) return Verdict(BowireUserMigrationState.AlreadyDecided, receipt: decided);

        if (mode == BowireUserMigrationMode.Skip) return Verdict(BowireUserMigrationState.Disabled);

        var entries = Walk(store.StorageRoot);
        if (entries.Count == 0) return Verdict(BowireUserMigrationState.NothingToMigrate);

        // Merging two sets of environments produces one set nobody can take
        // apart again, so a slot that already holds work is left alone.
        if (HasState(store.Slot)) return Verdict(BowireUserMigrationState.SlotNotEmpty);

        return Verdict(BowireUserMigrationState.Available, entries);
    }

    /// <summary>
    /// Carry out <paramref name="plan"/> and record it.
    /// </summary>
    /// <remarks>
    /// Copies into a staging directory beside the slot and moves it into place
    /// at the end. A file-by-file copy straight into the slot would, if it
    /// failed halfway, leave a slot that holds state — which the next
    /// <see cref="Plan"/> reads as <see cref="BowireUserMigrationState.SlotNotEmpty"/>
    /// and never offers again. Half the data, no receipt, and no way back
    /// through the product is a worse outcome than an error.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The plan is not on offer.</exception>
    public static BowireUserMigrationReceipt Apply(BowireUserMigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.State != BowireUserMigrationState.Available)
        {
            throw new InvalidOperationException(
                $"Nothing to apply: the migration for '{plan.Subject}' is {plan.State}, not Available.");
        }

        // Per attempt, not per slug: two browser tabs accepting at the same
        // moment would otherwise share one staging directory, and the second
        // one's opening cleanup would sweep the first one's half-copied tree.
        var staging = Path.Combine(
            plan.StorageRoot,
            BowireUserSlot.DirectoryName,
            StagingPrefix + plan.Slug + "-" + Guid.NewGuid().ToString("N")[..8]);

        var receipt = new BowireUserMigrationReceipt
        {
            Subject = plan.Subject,
            Outcome = BowireUserMigrationOutcome.Migrated,
            DecidedUtc = DateTimeOffset.UtcNow,
            Source = plan.StorageRoot,
            Files = plan.Entries.Count,
            Bytes = plan.Bytes,
        };

        var moved = false;
        try
        {
            Directory.CreateDirectory(staging);

            foreach (var entry in plan.Entries)
            {
                var source = SafePath.Combine(plan.StorageRoot, entry.RelativePath);
                var target = SafePath.Combine(staging, entry.RelativePath);
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.Copy(source, target, overwrite: true);
            }

            Write(Path.Combine(staging, ReceiptFileName), receipt);

            // An empty slot may already exist — a store touched it, or a
            // previous decline was withdrawn. Move needs the target gone.
            if (Directory.Exists(plan.Slot)) Delete(plan.Slot);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.Slot)!);
            Directory.Move(staging, plan.Slot);
            moved = true;
        }
        finally
        {
            // try/finally rather than catch-and-rethrow so the original
            // failure reaches the caller unwrapped, with the half-written
            // staging directory already gone.
            if (!moved) Delete(staging);
        }

        return receipt;
    }

    /// <summary>
    /// Record that <paramref name="plan"/> was refused, so it is not offered
    /// again.
    /// </summary>
    public static BowireUserMigrationReceipt Decline(BowireUserMigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var receipt = new BowireUserMigrationReceipt
        {
            Subject = plan.Subject,
            Outcome = BowireUserMigrationOutcome.Declined,
            DecidedUtc = DateTimeOffset.UtcNow,
            Source = plan.StorageRoot,
        };

        Directory.CreateDirectory(plan.Slot);
        Write(Path.Combine(plan.Slot, ReceiptFileName), receipt);
        return receipt;
    }

    /// <summary>
    /// The decision on record for the slot at <paramref name="slot"/>, or
    /// <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// An unreadable receipt reads as no receipt: the alternative is an
    /// identity that can never be offered a migration and never be told why,
    /// and re-offering is recoverable while that is not.
    /// </remarks>
    public static BowireUserMigrationReceipt? ReadReceipt(string slot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        var path = Path.Combine(slot, ReceiptFileName);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<BowireUserMigrationReceipt>(
                File.ReadAllText(path), s_json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return null;
        }
    }

    /// <summary>Every file under <paramref name="root"/> that belongs to a person.</summary>
    private static List<BowireUserMigrationEntry> Walk(string root)
    {
        var found = new List<BowireUserMigrationEntry>();
        if (!Directory.Exists(root)) return found;

        foreach (var top in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(top);
            if (NotPersonalState.Contains(name)) continue;

            if (Directory.Exists(top))
            {
                foreach (var file in Directory.EnumerateFiles(top, "*", s_walk))
                {
                    found.Add(Entry(root, file));
                }
            }
            else
            {
                found.Add(Entry(root, top));
            }
        }

        return found;
    }

    private static BowireUserMigrationEntry Entry(string root, string file)
        => new(
            Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
            new FileInfo(file).Length);

    /// <summary>True when the slot holds anything other than its receipt.</summary>
    private static bool HasState(string slot)
        => Directory.Exists(slot)
            && Directory.EnumerateFileSystemEntries(slot)
                .Any(e => !string.Equals(Path.GetFileName(e), ReceiptFileName, StringComparison.Ordinal));

    private static void Write(string path, BowireUserMigrationReceipt receipt)
        => File.WriteAllText(path, JsonSerializer.Serialize(receipt, s_json));

    private static void Delete(string directory)
    {
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort: a locked file here must not replace the
            // failure that brought us to the cleanup in the first place.
            _ = ex;
        }
    }
}
