// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>What an install should do about legacy single-user state (#97).</summary>
public enum BowireUserMigrationMode
{
    /// <summary>
    /// Offer it once and let the person decide. The default, because the
    /// first identity to sign in is not reliably the one the data belongs to.
    /// </summary>
    Prompt = 0,

    /// <summary>
    /// Migrate into the first identity that signs in, without asking. For
    /// installs where the operator knows there is exactly one person.
    /// </summary>
    Auto = 1,

    /// <summary>
    /// Never offer. For installs that want to start clean, and for the
    /// operator who has already moved the data by hand.
    /// </summary>
    Skip = 2,
}

/// <summary>Why a migration is or is not on offer (#97).</summary>
public enum BowireUserMigrationState
{
    /// <summary>There is legacy state, the slot is empty, and nobody has decided yet.</summary>
    Available = 0,

    /// <summary>The storage root holds nothing that belongs to a person.</summary>
    NothingToMigrate = 1,

    /// <summary>This identity already accepted or declined; the receipt says which.</summary>
    AlreadyDecided = 2,

    /// <summary>
    /// The slot already holds state. Copying into it would merge two sets of
    /// environments with no way to tell them apart afterwards.
    /// </summary>
    SlotNotEmpty = 3,

    /// <summary>The install set <see cref="BowireUserMigrationMode.Skip"/>.</summary>
    Disabled = 4,
}

/// <summary>How a decided migration ended (#97).</summary>
public enum BowireUserMigrationOutcome
{
    /// <summary>The state was copied into the slot.</summary>
    Migrated = 0,

    /// <summary>The offer was refused. Recorded so it is not made again.</summary>
    Declined = 1,
}

/// <summary>One file a migration would copy.</summary>
/// <param name="RelativePath">Path under the storage root, with <c>/</c> separators.</param>
/// <param name="Bytes">Size on disk, for the estimate shown before accepting.</param>
public sealed record BowireUserMigrationEntry(string RelativePath, long Bytes);

/// <summary>
/// What migrating the legacy single-user state into one identity's slot would
/// do — computed without touching anything (#97).
/// </summary>
public sealed class BowireUserMigrationPlan
{
    /// <summary>The identity this plan is for.</summary>
    public required string Subject { get; init; }

    /// <summary>The directory name <see cref="Subject"/> maps to.</summary>
    public required string Slug { get; init; }

    /// <summary>The storage root the legacy state sits in.</summary>
    public required string StorageRoot { get; init; }

    /// <summary>Absolute path to the slot the state would land in.</summary>
    public required string Slot { get; init; }

    /// <summary>Whether the migration is on offer, and if not, why not.</summary>
    public required BowireUserMigrationState State { get; init; }

    /// <summary>The files that would be copied. Empty unless <see cref="State"/> is Available.</summary>
    public IReadOnlyList<BowireUserMigrationEntry> Entries { get; init; } = [];

    /// <summary>The decision already on record, when there is one.</summary>
    public BowireUserMigrationReceipt? Receipt { get; init; }

    /// <summary>Total size of <see cref="Entries"/>.</summary>
    public long Bytes => Entries.Sum(e => e.Bytes);
}

/// <summary>
/// The record that a migration was decided, written into the slot it concerns.
/// </summary>
/// <remarks>
/// In the slot rather than in a central log, so that deleting an identity
/// deletes its receipt too: a central index of people who used to exist is
/// state nobody asked Bowire to keep. It is still an audit trail — it names
/// the subject, what was copied, from where, and when.
/// </remarks>
public sealed class BowireUserMigrationReceipt
{
    /// <summary>The identity that decided.</summary>
    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    /// <summary>Accepted or refused.</summary>
    [JsonPropertyName("outcome")]
    [JsonConverter(typeof(JsonStringEnumConverter<BowireUserMigrationOutcome>))]
    public required BowireUserMigrationOutcome Outcome { get; init; }

    /// <summary>When, in UTC.</summary>
    [JsonPropertyName("decidedUtc")]
    public required DateTimeOffset DecidedUtc { get; init; }

    /// <summary>Where the state came from.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>How many files were copied. Zero for a refusal.</summary>
    [JsonPropertyName("files")]
    public int Files { get; init; }

    /// <summary>How many bytes were copied. Zero for a refusal.</summary>
    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }

    /// <summary>The mode in force when the decision was made.</summary>
    [JsonPropertyName("mode")]
    [JsonConverter(typeof(JsonStringEnumConverter<BowireUserMigrationMode>))]
    public BowireUserMigrationMode Mode { get; init; }
}
