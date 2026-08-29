// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Scim;

/// <summary>A provisioning operation the current state does not allow.</summary>
public sealed class ScimConflictException : Exception
{
    /// <summary>A refused operation, unexplained.</summary>
    public ScimConflictException() { }

    /// <summary>A refused operation, with the reason a connector should log.</summary>
    public ScimConflictException(string message) : base(message) { }

    /// <summary>A refused operation, wrapping what actually failed.</summary>
    public ScimConflictException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// A provisioned identity as Bowire keeps it: the SCIM resource the connector
/// sees, plus the state only Bowire needs.
/// </summary>
/// <remarks>
/// An envelope rather than extra fields on <see cref="ScimUser"/>, because the
/// two audiences differ. <c>[JsonIgnore]</c> would keep Bowire's bookkeeping
/// out of the response and out of the file in the same stroke, which is how a
/// purge window silently stops working after a restart.
/// </remarks>
public sealed class ScimUserRecord
{
    /// <summary>What the connector sees.</summary>
    [JsonPropertyName("resource")]
    public ScimUser Resource { get; set; } = new();

    /// <summary>
    /// The token subject this record turned out to belong to, learned on first
    /// sign-in.
    /// </summary>
    /// <remarks>
    /// Not known at provisioning time and deliberately not guessed. The slot
    /// name is a function of the subject in the token; provisioning knows a
    /// <c>userName</c> and an <c>externalId</c>, and picking the wrong one
    /// creates a second slot whose data the person never sees.
    /// </remarks>
    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    /// <summary>When the identity was deprovisioned, for the purge window.</summary>
    [JsonPropertyName("deactivatedUtc")]
    public DateTimeOffset? DeactivatedUtc { get; set; }

    /// <summary>Where this identity's slot was moved when it was deprovisioned.</summary>
    [JsonPropertyName("archivedSlot")]
    public string? ArchivedSlot { get; set; }
}

/// <summary>
/// The provisioned user and group list, on disk (#96).
/// </summary>
/// <remarks>
/// <para>
/// One file per resource under <c>&lt;storage root&gt;/scim/</c>, with the
/// lookups an IdP needs held in memory. A single document holding every
/// record would have to be rewritten on each of the ten thousand writes a
/// first sync makes; a file per record makes a write cost one file and
/// leaves the read path — which is what the round-trip latency is actually
/// about — served from the index.
/// </para>
/// <para>
/// Every mutation also appends to <c>scim/events.jsonl</c>. Provisioning is
/// the one surface where "who removed this person, and when" gets asked
/// months later, and the resource files only ever show the current answer.
/// </para>
/// </remarks>
public sealed class BowireScimStore
{
    /// <summary>The directory under the storage root that holds provisioning state.</summary>
    public const string DirectoryName = "scim";

    /// <summary>Where a deprovisioned identity's slot is moved to wait out the purge window.</summary>
    private const string ArchivePrefix = ".deprovisioned-";

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ScimUserRecord> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScimGroup> _groups = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    private bool _loaded;

    /// <summary>
    /// A store rooted at <paramref name="storageRoot"/>.
    /// </summary>
    /// <param name="storageRoot">
    /// The data root — the same directory the identity slots live under, so
    /// deprovisioning can reach them.
    /// </param>
    /// <param name="clock">Injected in tests so a purge window can be crossed without waiting.</param>
    public BowireScimStore(string storageRoot, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        StorageRoot = Path.GetFullPath(storageRoot);
        Root = Path.Combine(StorageRoot, DirectoryName);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The data root this store's identities live under.</summary>
    public string StorageRoot { get; }

    /// <summary>Where the provisioning state is kept.</summary>
    public string Root { get; }

    /// <summary>The append-only record of every provisioning decision.</summary>
    public string EventLog => Path.Combine(Root, "events.jsonl");

    private string UsersDirectory => Path.Combine(Root, "users");

    private string GroupsDirectory => Path.Combine(Root, "groups");

    // ---- users ----

    /// <summary>Every provisioned identity, newest last.</summary>
    public IReadOnlyList<ScimUserRecord> Users()
    {
        EnsureLoaded();
        lock (_gate)
        {
            return _users.Values.OrderBy(r => r.Resource.Meta.Created).ToList();
        }
    }

    /// <summary>The record with this SCIM id, or <c>null</c>.</summary>
    public ScimUserRecord? GetUser(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        EnsureLoaded();
        lock (_gate) { return _users.GetValueOrDefault(id); }
    }

    /// <summary>
    /// The record for a token subject, or <c>null</c> when the identity was
    /// never provisioned.
    /// </summary>
    /// <remarks>
    /// Matched in the order the identifiers are trustworthy: the subject
    /// already bound to this record, then the IdP's own immutable id, then the
    /// login name. The last is the weakest — a person who changes their
    /// e-mail address changes their <c>userName</c> — which is exactly why the
    /// subject gets bound the first time it is seen.
    /// </remarks>
    public ScimUserRecord? FindBySubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        EnsureLoaded();

        lock (_gate)
        {
            return _users.Values.FirstOrDefault(r =>
                    Same(r.Subject, subject))
                ?? _users.Values.FirstOrDefault(r =>
                    Same(r.Resource.ExternalId, subject))
                ?? _users.Values.FirstOrDefault(r =>
                    Same(r.Resource.UserName, subject));
        }
    }

    /// <summary>The record with this login name, or <c>null</c>.</summary>
    public ScimUserRecord? FindByUserName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;
        EnsureLoaded();
        lock (_gate)
        {
            return _users.Values.FirstOrDefault(r => Same(r.Resource.UserName, userName));
        }
    }

    /// <summary>
    /// Provision a new identity.
    /// </summary>
    /// <exception cref="ScimConflictException">The login name is already taken.</exception>
    public ScimUserRecord CreateUser(ScimUser resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (string.IsNullOrWhiteSpace(resource.UserName))
        {
            throw new ScimConflictException("userName is required.");
        }

        EnsureLoaded();
        lock (_gate)
        {
            if (_users.Values.Any(r => Same(r.Resource.UserName, resource.UserName)))
            {
                throw new ScimConflictException(
                    $"A user with userName '{resource.UserName}' already exists.");
            }

            var now = _clock.GetUtcNow();
            resource.Id = Guid.NewGuid().ToString("D");
            EnsureSchema(resource.Schemas, ScimSchemas.User);
            resource.Meta = new ScimMeta
            {
                ResourceType = "User",
                Created = now,
                LastModified = now,
                Version = Etag(now),
            };

            var record = new ScimUserRecord { Resource = resource };
            _users[resource.Id] = record;
            Persist(record);
            Log("create", resource.Id, resource.UserName, active: resource.Active);
            return record;
        }
    }

    /// <summary>
    /// Replace an identity's attributes, keeping its id and creation time.
    /// </summary>
    /// <exception cref="ScimConflictException">The new login name belongs to somebody else.</exception>
    public ScimUserRecord? ReplaceUser(string id, ScimUser resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        EnsureLoaded();

        lock (_gate)
        {
            if (!_users.TryGetValue(id, out var existing)) return null;

            if (!string.IsNullOrWhiteSpace(resource.UserName)
                && _users.Values.Any(r => r.Resource.Id != id && Same(r.Resource.UserName, resource.UserName)))
            {
                throw new ScimConflictException(
                    $"A user with userName '{resource.UserName}' already exists.");
            }

            var wasActive = existing.Resource.Active;
            var now = _clock.GetUtcNow();

            resource.Id = id;
            EnsureSchema(resource.Schemas, ScimSchemas.User);
            resource.Meta = new ScimMeta
            {
                ResourceType = "User",
                Created = existing.Resource.Meta.Created,
                LastModified = now,
                Version = Etag(now),
            };

            existing.Resource = resource;
            ApplyActivation(existing, wasActive, resource.Active, now);
            Persist(existing);
            Log("replace", id, resource.UserName, active: resource.Active);
            return existing;
        }
    }

    /// <summary>
    /// Record the outcome of a partial update the caller has already applied.
    /// </summary>
    public ScimUserRecord? UpdateUser(string id, Action<ScimUser> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        EnsureLoaded();

        lock (_gate)
        {
            if (!_users.TryGetValue(id, out var existing)) return null;

            var wasActive = existing.Resource.Active;
            change(existing.Resource);

            if (_users.Values.Any(r => r.Resource.Id != id
                && Same(r.Resource.UserName, existing.Resource.UserName)))
            {
                throw new ScimConflictException(
                    $"A user with userName '{existing.Resource.UserName}' already exists.");
            }

            var now = _clock.GetUtcNow();
            existing.Resource.Id = id;
            existing.Resource.Meta.LastModified = now;
            existing.Resource.Meta.Version = Etag(now);

            ApplyActivation(existing, wasActive, existing.Resource.Active, now);
            Persist(existing);
            Log("update", id, existing.Resource.UserName, active: existing.Resource.Active);
            return existing;
        }
    }

    /// <summary>
    /// Deprovision an identity: deactivate it and start the purge window.
    /// </summary>
    /// <remarks>
    /// A soft delete, because deprovisioning is routinely undone — a team
    /// change, a misfiring sync, an extended contract. Deleting on the DELETE
    /// makes those recoverable only from a backup, if one exists.
    /// </remarks>
    /// <returns>Whether there was anything to deprovision.</returns>
    public bool DeleteUser(string id)
    {
        EnsureLoaded();
        lock (_gate)
        {
            if (!_users.TryGetValue(id, out var existing)) return false;

            var now = _clock.GetUtcNow();
            var wasActive = existing.Resource.Active;
            existing.Resource.Active = false;
            existing.Resource.Meta.LastModified = now;
            existing.Resource.Meta.Version = Etag(now);

            ApplyActivation(existing, wasActive, active: false, now);
            Persist(existing);
            Log("delete", id, existing.Resource.UserName, active: false);
            return true;
        }
    }

    /// <summary>
    /// Delete every identity whose purge window has run out, and their state.
    /// </summary>
    /// <returns>How many were purged.</returns>
    public int Purge(TimeSpan after)
    {
        EnsureLoaded();
        var purged = 0;

        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            foreach (var record in _users.Values.ToList())
            {
                if (record.DeactivatedUtc is not { } since) continue;
                if (now - since < after) continue;

                if (record.ArchivedSlot is not null) Delete(record.ArchivedSlot);
                File.Delete(UserFile(record.Resource.Id));
                _users.Remove(record.Resource.Id);
                Log("purge", record.Resource.Id, record.Resource.UserName, active: false);
                purged++;
            }
        }

        return purged;
    }

    /// <summary>
    /// Remember which token subject this record turned out to belong to.
    /// </summary>
    public void BindSubject(string id, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        EnsureLoaded();

        lock (_gate)
        {
            if (!_users.TryGetValue(id, out var existing)) return;
            if (Same(existing.Subject, subject)) return;

            existing.Subject = subject;
            Persist(existing);
            Log("bind", id, existing.Resource.UserName, active: existing.Resource.Active);
        }
    }

    // ---- groups ----

    /// <summary>Every provisioned group.</summary>
    public IReadOnlyList<ScimGroup> Groups()
    {
        EnsureLoaded();
        lock (_gate) { return _groups.Values.OrderBy(g => g.Meta.Created).ToList(); }
    }

    /// <summary>The group with this SCIM id, or <c>null</c>.</summary>
    public ScimGroup? GetGroup(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        EnsureLoaded();
        lock (_gate) { return _groups.GetValueOrDefault(id); }
    }

    /// <summary>Provision a new group.</summary>
    /// <exception cref="ScimConflictException">The display name is already taken.</exception>
    public ScimGroup CreateGroup(ScimGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (string.IsNullOrWhiteSpace(group.DisplayName))
        {
            throw new ScimConflictException("displayName is required.");
        }

        EnsureLoaded();
        lock (_gate)
        {
            if (_groups.Values.Any(g => Same(g.DisplayName, group.DisplayName)))
            {
                throw new ScimConflictException(
                    $"A group named '{group.DisplayName}' already exists.");
            }

            var now = _clock.GetUtcNow();
            group.Id = Guid.NewGuid().ToString("D");
            EnsureSchema(group.Schemas, ScimSchemas.Group);
            group.Meta = new ScimMeta
            {
                ResourceType = "Group",
                Created = now,
                LastModified = now,
                Version = Etag(now),
            };

            _groups[group.Id] = group;
            PersistGroup(group);
            Log("create-group", group.Id, group.DisplayName, active: true);
            return group;
        }
    }

    /// <summary>Replace a group, keeping its id and creation time.</summary>
    public ScimGroup? ReplaceGroup(string id, ScimGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        EnsureLoaded();

        lock (_gate)
        {
            if (!_groups.TryGetValue(id, out var existing)) return null;

            if (!string.IsNullOrWhiteSpace(group.DisplayName)
                && _groups.Values.Any(g => g.Id != id && Same(g.DisplayName, group.DisplayName)))
            {
                throw new ScimConflictException(
                    $"A group named '{group.DisplayName}' already exists.");
            }

            var now = _clock.GetUtcNow();
            group.Id = id;
            EnsureSchema(group.Schemas, ScimSchemas.Group);
            group.Meta = new ScimMeta
            {
                ResourceType = "Group",
                Created = existing.Meta.Created,
                LastModified = now,
                Version = Etag(now),
            };

            _groups[id] = group;
            PersistGroup(group);
            Log("replace-group", id, group.DisplayName, active: true);
            return group;
        }
    }

    /// <summary>Apply a change to a group in place.</summary>
    public ScimGroup? UpdateGroup(string id, Action<ScimGroup> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        EnsureLoaded();

        lock (_gate)
        {
            if (!_groups.TryGetValue(id, out var existing)) return null;

            change(existing);
            var now = _clock.GetUtcNow();
            existing.Id = id;
            existing.Meta.LastModified = now;
            existing.Meta.Version = Etag(now);

            PersistGroup(existing);
            Log("update-group", id, existing.DisplayName, active: true);
            return existing;
        }
    }

    /// <summary>Remove a group. Groups carry no state of their own, so this is a hard delete.</summary>
    public bool DeleteGroup(string id)
    {
        EnsureLoaded();
        lock (_gate)
        {
            if (!_groups.TryGetValue(id, out var existing)) return false;

            File.Delete(GroupFile(id));
            _groups.Remove(id);
            Log("delete-group", id, existing.DisplayName, active: false);
            return true;
        }
    }

    /// <summary>
    /// Whether the identity with this SCIM id is a member of
    /// <paramref name="adminGroup"/>.
    /// </summary>
    public bool IsMemberOf(string userId, string adminGroup)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(adminGroup)) return false;
        EnsureLoaded();

        lock (_gate)
        {
            return _groups.Values
                .Where(g => Same(g.DisplayName, adminGroup))
                .SelectMany(g => g.Members)
                .Any(m => string.Equals(m.Value, userId, StringComparison.Ordinal));
        }
    }

    // ---- persistence ----

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            Load(UsersDirectory, file =>
            {
                var record = JsonSerializer.Deserialize<ScimUserRecord>(File.ReadAllText(file), s_json);
                if (record is not null && !string.IsNullOrEmpty(record.Resource.Id))
                {
                    _users[record.Resource.Id] = record;
                }
            });
            Load(GroupsDirectory, file =>
            {
                var group = JsonSerializer.Deserialize<ScimGroup>(File.ReadAllText(file), s_json);
                if (group is not null && !string.IsNullOrEmpty(group.Id)) _groups[group.Id] = group;
            });
            _loaded = true;
        }
    }

    private static void Load(string directory, Action<string> read)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try { read(file); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // One unreadable record must not take the whole user list down
                // with it: an install that cannot start because of a single
                // corrupt file locks everybody out, which is a worse failure
                // than one identity being missing and re-provisioned.
                _ = ex;
            }
        }
    }

    private string UserFile(string id) => Path.Combine(UsersDirectory, id + ".json");

    private string GroupFile(string id) => Path.Combine(GroupsDirectory, id + ".json");

    private void Persist(ScimUserRecord record)
    {
        Directory.CreateDirectory(UsersDirectory);
        File.WriteAllText(UserFile(record.Resource.Id), JsonSerializer.Serialize(record, s_json));
    }

    private void PersistGroup(ScimGroup group)
    {
        Directory.CreateDirectory(GroupsDirectory);
        File.WriteAllText(GroupFile(group.Id), JsonSerializer.Serialize(group, s_json));
    }

    /// <summary>
    /// Move the identity's slot out of the way when it is deprovisioned, and
    /// back when it is not.
    /// </summary>
    /// <remarks>
    /// Moved rather than deleted, and only once the purge window closes is
    /// anything destroyed. Reactivation before then puts the slot back where
    /// the person left it, which is what makes "deactivate" a reversible
    /// operation rather than a polite word for delete.
    /// </remarks>
    private void ApplyActivation(ScimUserRecord record, bool wasActive, bool active, DateTimeOffset now)
    {
        if (wasActive && !active)
        {
            record.DeactivatedUtc = now;
            record.ArchivedSlot = Archive(record);
            return;
        }

        if (!wasActive && active)
        {
            Restore(record);
            record.DeactivatedUtc = null;
            record.ArchivedSlot = null;
        }
    }

    private string? Archive(ScimUserRecord record)
    {
        var slot = SlotOf(record);
        if (slot is null || !Directory.Exists(slot)) return null;

        var target = Path.Combine(
            StorageRoot,
            BowireUserSlot.DirectoryName,
            ArchivePrefix + Path.GetFileName(slot) + "-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.Move(slot, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The record still says deactivated, and the gate reads the record
            // rather than the directory — so a slot that could not be moved
            // does not leave the identity able to sign in.
            _ = ex;
            return null;
        }
    }

    private void Restore(ScimUserRecord record)
    {
        if (record.ArchivedSlot is not { } archived || !Directory.Exists(archived)) return;

        var slot = SlotOf(record);
        if (slot is null || Directory.Exists(slot)) return;

        try { Directory.Move(archived, slot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
    }

    /// <summary>
    /// Where this identity's state lives, or <c>null</c> when nobody has
    /// signed in as them yet and there is nothing to move.
    /// </summary>
    private string? SlotOf(ScimUserRecord record)
    {
        var subject = record.Subject;
        if (string.IsNullOrWhiteSpace(subject)) return null;
        return new ScopedBowireUserStore(StorageRoot, subject).Slot;
    }

    private static void Delete(string directory)
    {
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
    }

    private void Log(string action, string id, string name, bool active)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var line = JsonSerializer.Serialize(new
            {
                at = _clock.GetUtcNow(),
                action,
                id,
                name,
                active,
            });
            File.AppendAllText(EventLog, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: losing the audit line must not fail the
            // provisioning call the IdP is waiting on, which it would then
            // retry forever.
            _ = ex;
        }
    }

    /// <summary>
    /// Make sure the resource names its own schema, without disturbing any
    /// extension URNs the connector also declared.
    /// </summary>
    private static void EnsureSchema(List<string> schemas, string required)
    {
        if (schemas.Any(s => string.Equals(s, required, StringComparison.OrdinalIgnoreCase))) return;
        schemas.Insert(0, required);
    }

    private static string Etag(DateTimeOffset at)
        => "W/\"" + at.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"";

    private static bool Same(string? left, string? right)
        => left is not null && right is not null
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
