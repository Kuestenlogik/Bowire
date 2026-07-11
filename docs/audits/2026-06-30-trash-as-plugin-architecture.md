---
summary: "Architecture audit for extracting Bowire's Trash subsystem into a pluggable extension point in v2.3 — current inventory, coupling map, proposed IBowireTrashStore + IBowireTrashContributor<T> interfaces, conservative migration plan."
---

# Trash-as-Plugin Architecture Audit (v2.3 prep)

> Read-only design audit. No code changes accompany this document.
> Baseline commit: `df638e61` (refactor(action-log): decouple snapshot from Trash — W2a).
> Operator question: *"könnte der Papierkorb vielleicht sogar selbst ein Plugin sein?"*
> v2.2 R3 decoupled the action log from Trash; v2.3 can extract Trash itself.

## Section 1 — Current state inventory

### 1.1 Per-entity Trash buckets

Every Trash today is a JS array on the client, persisted to `localStorage`, holding entries of shape `{ entry|workspace, deletedAt, originalIdx, ...payload }`. There is no server-side Trash.

| Entity | Array name | Storage key | Scope | Restore | Hard-purge |
|---|---|---|---|---|---|
| Workspaces | `workspacesTrash` | `bowire_workspaces_trash` | App-wide (global LS key — the workspace IS the thing deleted) | `restoreWorkspaceFromTrash(t)` (prologue.js:2979) | `purgeWorkspaceFromTrash(t)` (prologue.js:3047) |
| Collections | `collectionsTrash` | `bowire_collections_trash` (via `wsKey(...)`) | Workspace-scoped | inline restore in `kindConfig.collections.onRestore` (render-env-auth.js:1720); per-list in `renderTrashSection` (render-sidebar.js:3449) | inline splice + `persistCollectionsTrash()` |
| Recordings | `recordingsTrash` | `bowire_recordings_trash` (via `wsKey(...)`) | Workspace-scoped | `kindConfig.recordings.onRestore` (render-env-auth.js:1698); per-list `renderTrashSection` | inline splice + `persistRecordingsTrash()` |
| Request tabs | `tabsTrash` | `bowire_request_tabs_trash` (via `wsKey(...)`) | Workspace-scoped, ring-capped (`TABS_TRASH_MAX`) | `restoreClosedTab(t)` (prologue.js:5311) | inline splice + `persistTabsTrash()` |

### 1.2 Gaps — entities WITHOUT a Trash today

These have a hard-delete path but no soft-delete bucket:

| Entity | Today | Note |
|---|---|---|
| Flows | hard-delete; undo path uses an action-log snapshot only (no `flowsTrash` array) | prologue.js:3654 comment confirms: *"flows don't have a soft-delete trash array; the snapshot path…"* |
| Benchmarks | hard-delete; no `benchmarksTrash` | confirmed by grep — zero hits for `benchmarksTrash` |
| Environments | hard-delete; no `environmentsTrash`/`envsTrash` | confirmed by grep |
| Mocks | hard-delete; "the host" owns the lifecycle, no trash array | prologue.js:3721 comment: *"there's no trash array for mocks (the host owns them)"* |
| Server URLs / aliases | hard-delete via list edits | no soft-delete UX |
| Favorites / history | "Clear" buttons routed via action-log undo, not via a trash bucket | Settings → Data: `Clear call history` / `Clear favorites` (settings.js:3123/3142) |

The gap pattern is consistent: **Trash is present for objects an operator carefully composed (workspaces, collections, recordings, tabs)** and absent for objects that are cheap to recreate (env vars, server URLs) or already ephemeral (history, favorites).

### 1.3 UI surfaces that depend on Trash

| Surface | Lives in | Reads | Writes |
|---|---|---|---|
| Global Trash drawer (topbar icon → modal) | `renderGlobalTrashOverlay()` (render-env-auth.js:1626) | all four buckets via `kindConfig` map | `onRestore` / `onDeleteForever` per kind |
| Per-list "Recently deleted" sidebar section | `renderTrashSection(opts)` (render-sidebar.js:3449) | one bucket via `opts.trashArray` | splice + `opts.persist()` |
| Recordings sidebar — bulk delete spawns trash rows | render-sidebar.js:1532-1717 | `recordingsTrash` | unshift + persist |
| Collections sidebar — same pattern | render-sidebar.js (via `renderTrashSection`) | `collectionsTrash` | same |
| Settings → Data → "Clear recordings" | settings.js:3166 | reads `recordingsList` | unshifts into `recordingsTrash` for undo-restore |
| Settings → Data → "Clear collections" | settings.js:3229 | reads `collectionsList` | unshifts into `collectionsTrash` |
| Settings → Data → "Workspace deletion mode" (soft/hard) | settings.js:3070 | `bowire_workspace_delete_mode` | drives whether `deleteWorkspace` populates `workspacesTrash` |
| Settings → Data → "Trash retention" (7/14/30/never) | settings.js:3097 | `bowire_trash_retention_days` | drives `purgeOldTrash()` / `purgeOldTabsTrash()` |
| Topbar trash icon + filter chip state | persisted under `bowire_trash_filter_state` (prologue.js:1455) | per-kind visibility | dropdown UI only |
| Workspace name-collision check | `_isWorkspaceNameTaken` + `_toastIfTrashCollision` (prologue.js:2282/2296) | `workspacesTrash` | none (read-only — collision message) |

### 1.4 Retention sweep (auto-purge timer)

Two boot-time IIFEs:

- `purgeOldTrash()` (prologue.js:1400) — filters `recordingsTrash` + `collectionsTrash` by `deletedAt > cutoff`.
- `purgeOldTabsTrash()` (prologue.js:1427) — same for `tabsTrash`, plus a ring-cap.

Cutoff comes from `_readTrashRetention()` (settings.js:3052) — `7|14|30|'never'`, default 30 days. There is currently **no** equivalent sweep call for `workspacesTrash` (the W2 retention setting documents that it applies to "soft-deleted workspaces" but the boot-time sweeps were not updated). This is a pre-existing inconsistency worth surfacing in v2.3.

## Section 2 — Coupling map (after #337 / R3)

What in CORE still depends on Trash today? For each: does it degrade gracefully if Trash is absent, or does it break?

| Coupling point | Location | Without Trash plugin → |
|---|---|---|
| `deleteWorkspace` writes to `workspacesTrash` in soft mode | prologue.js:3082 | **Graceful** — already gated on `mode !== 'hard'`. Forcing hard mode when no plugin is loaded is the natural degradation. The action-log entry already carries the snapshot (W2a). |
| `deleteCollection` writes to `collectionsTrash` | prologue.js:5976 | **Graceful** with a typeof-guard already in place (`typeof collectionsTrash !== 'undefined'`); without it the collection just hard-deletes. |
| Recording-rail soft-delete writes to `recordingsTrash` | render-sidebar.js:1532, prologue.js:3393+ | **Graceful** — every write is wrapped in `Array.isArray(recordingsTrash)`. |
| `closeTab` pushes into `tabsTrash` | prologue.js:5242 | **Graceful** — try/catch; closing a tab still works, "Recently closed" just goes blank. |
| Action-log resolvers fall back to Trash for legacy entries | prologue.js:3243 (`_findTrashEntryByWorkspaceId`), 3275, 3313, 3578 | **Graceful** — every resolver already prefers the inline snapshot (W2a); the trash walk is the fallback for entries persisted under v2.1. Without Trash, those legacy entries surface a toast (`"Could not restore workspace — trash entry purged."`) — that's the existing behavior when the entry IS already gone, so it degrades to the same message. |
| `_isWorkspaceNameTaken` consults `workspacesTrash` | prologue.js:2282 | **Graceful** — without Trash, two workspaces can share a name across a delete-recreate cycle. Same as today's hard-delete behavior. |
| `_toastIfTrashCollision` toasts on name reuse | prologue.js:2296 | **Graceful** — guarded on `Array.isArray(workspacesTrash)`; otherwise the standard "name taken" toast wins. |
| Global Trash drawer (`renderGlobalTrashOverlay`) | render-env-auth.js:1626 | **Breaks the surface** — overlay is meaningless with zero buckets. Should hide the topbar trash icon entirely when no Trash contributor is registered. |
| Per-list "Recently deleted" sidebar sections | render-sidebar.js:3449 | **Graceful** — function early-returns on empty array; with no plugin, no array, no section. |
| Settings → Data → soft/hard/retention rows | settings.js:3070–3121 | **Should hide** — the dropdowns become inert (soft mode silently behaves like hard). Better UX: omit the rows when no Trash plugin is registered. |
| Settings → Data → Clear recordings / collections (undo via Trash) | settings.js:3166, 3229 | **Partial degradation** — the bulk clear still works; the undo toast restores from the snapshot it captured in-closure, but the trash rows would never appear. Acceptable: undo within toast still works; persistent recovery is lost. |
| Retention sweep IIFEs at boot | prologue.js:1400, 1427 | **No-op** if arrays are undefined — guarded today by inline declarations. With plugin gone, the sweeps don't run, but there's nothing to sweep either. |

**Summary of coupling assessment**: After the v2.2 W2a action-log decouple, Core can run without Trash. The one surface that needs explicit handling is the topbar Trash icon + Settings → Data rows — both should hide when no contributor registers. Every other path already degrades to hard-delete behavior via its existing guards.

## Section 3 — Proposed plugin architecture

Two extension points. Following the existing house style (cf. `IBowireProtocol`, `IBowireUiExtension` in `docs/architecture/plugin-architecture.md`): C# contracts shipped in core, discovered by assembly scan, opt-in via package reference. Default implementation lives in a new sibling package, included in `Bundle.Workbench`.

### 3.1 `IBowireTrashStore` — persistence + retention

One implementation per backing store. Default: `BrowserLocalStorageTrashStore`. Future: `SqliteTrashStore` (embedded host), `S3TrashStore` (enterprise compliance archive), `AuditLogTrashStore` (write-through to an enterprise audit log so deletes are immutable).

```csharp
public interface IBowireTrashStore
{
    /// <summary>Stable identifier, e.g. "browser-localstorage", "sqlite", "s3".</summary>
    string Id { get; }

    /// <summary>Human-readable label rendered in Settings → Data.</summary>
    string DisplayName { get; }

    /// <summary>Called once at workbench boot before any contributor runs.</summary>
    Task InitializeAsync(IServiceProvider services, CancellationToken ct = default);

    /// <summary>Append a freshly soft-deleted entry to the given bucket.</summary>
    Task<TrashEntry> PushAsync(string bucketId, TrashEntry entry, CancellationToken ct = default);

    /// <summary>Enumerate live entries for one bucket, newest-first.</summary>
    IAsyncEnumerable<TrashEntry> ListAsync(string bucketId, CancellationToken ct = default);

    /// <summary>Drop a single entry without restoring (hard-purge from trash).</summary>
    Task PurgeAsync(string bucketId, string entryId, CancellationToken ct = default);

    /// <summary>Drop every entry across every bucket. Used by Settings → Data → Empty Trash.</summary>
    Task PurgeAllAsync(CancellationToken ct = default);

    /// <summary>Apply the operator-configured retention policy. Implementations decide whether they sweep at boot, on a timer, or both.</summary>
    Task ApplyRetentionAsync(TrashRetentionPolicy policy, CancellationToken ct = default);

    /// <summary>Notify subscribers when entries are added / removed by another tab (browser) or another process (sqlite).</summary>
    event EventHandler<TrashStoreChanged>? Changed;
}
```

Notes on the surface:

- `TrashEntry` is the existing `{ id, bucketId, deletedAt, payload, originalIdx? }` shape, frozen as a record type. `payload` stays a JSON blob — the store doesn't need to know what's inside, the contributor does.
- The `Changed` event is what makes the cross-tab sync we already have in Core (`storage` event on `window`) generalisable to non-browser backends.
- No `RestoreAsync` — restore is the contributor's job (the store returns the bytes; the contributor knows how to re-insert into `recordingsList` at `originalIdx`).

### 3.2 `IBowireTrashContributor<T>` — per-entity-type registration

Each entity type (workspaces, collections, recordings, request tabs, and **future flows / benchmarks / environments** — see §1.2 gaps) declares its bucket via a contributor.

```csharp
public interface IBowireTrashContributor<T> where T : class
{
    /// <summary>Bucket id — namespaces the entries inside whichever store is active.
    /// Matches today's storage keys minus the bowire_ prefix: "workspaces", "collections", "recordings", "request_tabs", …</summary>
    string BucketId { get; }

    /// <summary>Label rendered in the Global Trash drawer filter chip + the per-list "Recently deleted" header.</summary>
    string DisplayLabel { get; }

    /// <summary>Workspace-scoped buckets persist under wsKey(...); app-wide buckets persist globally.
    /// Workspaces themselves are app-wide; everything else is workspace-scoped today.</summary>
    TrashBucketScope Scope { get; }

    /// <summary>Render a human-readable row name from a payload. Falls back to "(unnamed X)".</summary>
    string DisplayName(T entry);

    /// <summary>Re-insert the payload at its original index. Returns true on success.</summary>
    Task<bool> RestoreAsync(TrashEntry<T> entry, CancellationToken ct = default);

    /// <summary>Optional pre-purge hook for entities with off-localStorage side effects (e.g. recording chunk files on disk).
    /// Workspaces use this today to call _purgeWorkspaceDiskStorage.</summary>
    Task OnHardPurgeAsync(TrashEntry<T> entry, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Optional collision-checker. Workspaces return true when a live name matches a trashed name so the UI can prompt the operator.</summary>
    bool CollidesWithLive(T candidate, IReadOnlyList<TrashEntry<T>> trashed) => false;
}
```

A contributor registers via `[BowireExtension]` on its type, mirroring the existing protocol-plugin pattern. The default contributors (workspace + collection + recording + request-tab) live in the new `Kuestenlogik.Bowire.Trash` package alongside the default `IBowireTrashStore` impl.

### 3.3 Sequence — soft-delete with Trash plugin loaded

```
Operator                Workbench (JS)           Trash plugin host         Trash store
  │  click "Delete WS"     │                          │                         │
  ├───────────────────────▶│                          │                         │
  │                        │ getWorkspaceDeleteMode() │                         │
  │                        │ === 'soft'               │                         │
  │                        │                          │                         │
  │                        │ /api/trash/push          │                         │
  │                        │   bucket=workspaces      │                         │
  │                        │   payload={ws,data,...}  │                         │
  │                        ├─────────────────────────▶│                         │
  │                        │                          │ contributor.OnPush      │
  │                        │                          ├────────────────────────▶│
  │                        │                          │                         │ persist
  │                        │                          │◀────────────────────────┤
  │                        │                          │ Changed event           │
  │                        │◀─────────────────────────┤                         │
  │                        │ update workspacesTrash   │                         │
  │                        │ re-render Global Trash   │                         │
  │  toast "Restored?"     │                          │                         │
  │◀───────────────────────┤                          │                         │
```

### 3.4 Sequence — soft-delete with Trash plugin NOT loaded (degraded path)

```
Operator                Workbench (JS)
  │  click "Delete WS"     │
  ├───────────────────────▶│
  │                        │ trashStore === null
  │                        │ effectiveMode = 'hard' (forced)
  │                        │
  │                        │ deleteWorkspace(id) (hard branch)
  │                        │   ├─ _purgeWorkspaceData(id)
  │                        │   ├─ _purgeWorkspaceDiskStorage(id)
  │                        │   └─ action-log entry carries snapshot for Ctrl+Z window
  │                        │
  │  toast "Deleted —      │
  │   Undo (10s)"          │
  │◀───────────────────────┤
```

Forcing hard mode when no plugin is registered is the conservative answer: the action-log Undo window (already snapshot-bearing post-W2a) still works for the brief recovery window, but the persistent Trash drawer + 30-day retention are gone. Settings → Data hides the soft/hard/retention dropdowns when `trashStore === null` so the operator isn't presented with inert controls.

## Section 4 — Migration plan

Conservative migration. Goal: no operator loses Trash data on upgrade, regardless of which packages they install.

### 4.1 Storage-format compatibility

The new default `BrowserLocalStorageTrashStore` **reads the same localStorage keys** the v2.2 code writes:

- `bowire_workspaces_trash`
- `wsKey('bowire_collections_trash')` per workspace
- `wsKey('bowire_recordings_trash')` per workspace
- `wsKey('bowire_request_tabs_trash')` per workspace
- `bowire_trash_retention_days` (policy)
- `bowire_workspace_delete_mode` (policy)
- `bowire_trash_filter_state` (UI state)

No key renames, no shape changes to entries. The entry shape (`{ entry|workspace, deletedAt, originalIdx, ... }`) becomes the canonical `TrashEntry.Payload`; the store wraps it without rewriting.

### 4.2 Bootstrap on first run with plugin installed

Operator on v2.2 → v2.3 upgrade with `Bundle.Workbench` (default contributors auto-load):

1. `BrowserLocalStorageTrashStore.InitializeAsync` scans the existing keys.
2. Existing arrays are surfaced as-is — no migration, no copy. The store reads *in place*.
3. The retention sweep runs against the same TTL setting.
4. The Global Trash drawer renders the same buckets via the new contributor list.

Operator sees: no behavioural change. The audit doc, the topbar icon, and the Settings → Data rows all look identical. The reorg is invisible from the UI.

### 4.3 Installing the plugin LATER (operator upgraded core but not the Trash sibling)

If the operator drops `Kuestenlogik.Bowire.Trash` from their package selection (Bundle.Minimal embedded host, custom assembly):

1. Workbench boot detects no `IBowireTrashStore` registered.
2. Settings → Data conditionally hides the soft/hard/retention rows.
3. The topbar trash icon is hidden.
4. `deleteWorkspace` forces hard mode (with the action-log Undo still available).
5. **Existing `bowire_*_trash` keys are left in place** — not migrated, not deleted. If the operator re-installs the Trash plugin later, the buckets re-appear with their original contents.

This preserves the "no data lost" promise without forcing core to know how to read Trash.

### 4.4 Uninstalling the plugin

Two scenarios:

| Scenario | Behaviour |
|---|---|
| Operator removes `Kuestenlogik.Bowire.Trash` from `Bundle.Workbench` consumers | Trash is gone from the UI. `bowire_*_trash` keys remain in localStorage. Re-installing restores access. Operator can clear the keys manually via `localStorage.clear()` or via a one-time CLI helper (`bowire trash purge --all` — out of scope for v2.3 P1). |
| Operator swaps default store for `SqliteTrashStore` | One-way migration helper at first boot: the new store reads existing localStorage buckets via the same key list and re-persists into SQLite. Original localStorage keys are kept for one minor (v2.3.x) as a fallback, removed in v2.4. |

This is the conservative answer to the "what if they switch backends later?" risk — give them one release where both stores agree.

## Section 5 — Open questions + risks

| # | Question / risk | What I'd research | Who can answer |
|---|---|---|---|
| Q1 | Server-side Trash for enterprise tenants — is there appetite for an audit-log backed store where deletes are immutable and only an admin can hard-purge? | Talk to embedded-host operators about SOC2 / GDPR requirements. Sketch `IBowireTrashStore` impl backed by a write-once log. | Product / enterprise lead |
| Q2 | Today the **workspaces** retention sweep is missing (see §1.4) — the W2 setting docs claim it applies to workspaces, but `purgeOldTrash()` only filters recordings + collections. Fix it inside the plugin extraction, or in a separate hot-fix first? | Decide whether to fix it pre-extraction (cleaner baseline) or roll it into the v2.3 PRs (one atomic change-set). | Maintainer call |
| Q3 | Cross-tab sync — today the `storage` event handles this implicitly (browser broadcasts localStorage writes). Does `IBowireTrashStore.Changed` need a cross-tab transport, or do we leave that to the impl? | Verify the existing behaviour against multi-tab Bowire (two tabs open, delete in one, second tab's drawer state) and confirm parity is required. | Test + verify before designing the event surface |
| Q4 | Per-entity-type *vs* per-bucket-id — is `IBowireTrashContributor<T>` worth the generic, or should we follow `IBowireProtocol`'s pattern of one non-generic interface with a `BucketId` discriminator? | Read the call sites in `renderGlobalTrashOverlay` — they already key off `_kind` strings, so generic typing is mostly a compile-time nicety on the host side. | Author + reviewer |
| Q5 | Should soft-delete travel through `/bowire/api/trash/*` HTTP endpoints (mirroring the `/bowire/api/services` shape) or stay JS-only? | Embedded hosts with sidecar Bowire may need server-side stores. Sidecar-only deploys would force the API surface. | Embedded-host operator survey |
| Q6 | Bulk-delete UX (Settings → Data → Clear recordings) currently shoves N entries into trash and pops a toast. Each push fires `persist*Trash` once. With a remote store, this becomes N round-trips. Need a batch push API. | Add `PushManyAsync` to `IBowireTrashStore` or document the loop as N writes acceptable for local stores and recommended-batched for remote. | API design |
| Q7 | The `_lastWorkspaceDeleteSnapshot` side channel (prologue.js:2857) was added for the W2a decouple — does the plugin extraction let us retire it, or does the toast callback still need an in-memory hand-off? | Trace the toast → action-log → snapshot flow once the plugin is live. The side channel may collapse into the `TrashEntry` return value of `PushAsync`. | Implementation phase |
| R1 | **Risk**: Embedded hosts on Bundle.Minimal silently lose soft-delete. Need explicit doc + release-note callout. | Draft the release-note copy now so it's not an afterthought. | Docs |
| R2 | **Risk**: A store backed by SQLite or S3 can fail mid-restore (network, file lock). The contributor's `RestoreAsync` returns `Task<bool>`; the UI needs a richer error type to distinguish "transient retry" from "payload corrupt". | Define a `TrashRestoreResult` discriminated record. | Design phase |
| R3 | **Risk**: The legacy action-log entries (pre-W2a) walk `workspacesTrash` to find a snapshot (prologue.js:3275). After plugin extraction these still need to resolve. | Keep the fallback path in core, but have it consult the registered store via the host-bridge rather than the global JS array. | Implementation |

### 5.1 — Triage decisions (v2.3)

The three highest-impact questions above were resolved at issue triage; recording them here so the implementation PRs (§6) don't re-litigate:

- **D1 (→ Q4) — Keep the generic `IBowireTrashContributor<T>`.** The generic earns its keep on the `RestoreAsync(TrashEntry<T>)` payload typing; the flat string-discriminator that `renderGlobalTrashOverlay` already uses is exposed as a **non-generic `BucketId`** property on the contributor so the registry (`BowireTrashRegistry`) can key off it without reflecting `T`. Mirrors `IBowireProtocol` for discovery while keeping compile-time payload safety on the host side.
- **D2 (→ Q2) — Fix the workspaces-retention gap inside the extraction, in PR 4.** No pre-extraction hot-fix; `IBowireTrashStore.ApplyRetentionAsync` sweeps **all** buckets (workspaces included) uniformly, closing the §1.4 gap as part of the same atomic change-set rather than as a separate patch that the extraction would then rewrite.
- **D3 (→ Q1 / Q5) — Ship the interface in v2.3, one default impl in v2.3, richer stores in v2.4.** `IBowireTrashStore` + the `BrowserLocalStorageTrashStore` default land in v2.3 (PRs 1–2). Server-side / audit-log-backed / SQLite stores (immutable deletes, admin-only purge, the `/bowire/api/trash/*` surface) are **v2.4 sibling packages** against the same interface — so v2.3 doesn't block on the enterprise-store design.

## Section 6 — Recommended v2.3 PR sequence

Five independently landable PRs. Each ends in a green workbench; none requires a follow-up to be safe to ship.

### PR 1 — Declare contracts in Core

Add `IBowireTrashStore`, `IBowireTrashContributor<T>`, `TrashEntry`, `TrashBucketScope`, `TrashRetentionPolicy`, `TrashStoreChanged`, `TrashRestoreResult` under `Kuestenlogik.Bowire/Trash/`. Wire the registry (`BowireTrashRegistry.Discover()`) but leave it unused. **No JS changes.** No behaviour change — just the seam.

Acceptance: assembly scan finds zero contributors by default; existing `workspacesTrash` / `collectionsTrash` / … paths still work.

### PR 2 — Default `Kuestenlogik.Bowire.Trash` package

New project alongside `Kuestenlogik.Bowire.Workspaces`. Ships:

- `BrowserLocalStorageTrashStore` (reads/writes the existing `bowire_*_trash` keys).
- Four `[BowireExtension]`-marked contributors: `WorkspaceTrashContributor`, `CollectionTrashContributor`, `RecordingTrashContributor`, `RequestTabTrashContributor`.
- Add the package to `Bundle.Workbench` ProjectReference list.

**No JS changes yet** — the JS arrays still drive the UI. Plugin host is parallel-live, just not consulted. Acceptance: registry returns one store + four contributors when the bundle is loaded; zero when only Bundle.Minimal is loaded.

### PR 3 — Cut over Workspaces + Collections

Replace inline `workspacesTrash.unshift(...)` / `collectionsTrash.unshift(...)` and their per-list restore handlers with calls into the host-bridge (`bowireTrashPush(bucket, payload)` / `bowireTrashList(bucket)`). The bridge round-trips to the registered `IBowireTrashStore` for hosts that have one, or no-ops + forces hard-delete for hosts that don't.

Per-list "Recently deleted" sidebar sections keep their renderer but read from the bridge. Retention sweep moves to `IBowireTrashStore.ApplyRetentionAsync`.

Acceptance: deleting a workspace or collection persists via the new bridge; restoring round-trips correctly; Bundle.Minimal host hard-deletes silently with the existing action-log Undo as the only safety net.

### PR 4 — Cut over Recordings + Request tabs

Same shape as PR 3, smaller scope. After this PR the JS bucket arrays (`recordingsTrash`, `tabsTrash`) are read-through caches of the bridge — Core no longer mutates them directly.

Fix Q2's missing workspace-retention sweep at the same time (`ApplyRetentionAsync` covers all four buckets uniformly).

Acceptance: Global Trash drawer renders all four buckets via the bridge; closing a tab + restoring from the drawer round-trips correctly. Multi-tab sync still works.

### PR 5 — Remove the dead bucket arrays from Core

Drop `workspacesTrash`, `collectionsTrash`, `recordingsTrash`, `tabsTrash` declarations + their `persist*Trash` functions from `prologue.js`. The bridge owns reads + writes; the in-memory caches live inside the bridge module.

Conditionally hide Settings → Data soft/hard/retention rows + the topbar trash icon when no `IBowireTrashStore` is registered. Update the release notes.

Acceptance: Bundle.Workbench host behaves identically to v2.2; Bundle.Minimal host shows no Trash surface; uninstalling `Kuestenlogik.Bowire.Trash` from the bundle survives a workbench restart.

### Sequencing notes

- PRs 1–2 are reviewable as design-only changes — useful pivot point if the surface needs revision.
- PRs 3–4 are the riskiest (live state migration). Land them on consecutive workdays so a regression in PR 3 surfaces before PR 4 widens the blast radius.
- PR 5 is delete-only. If we're nervous about hidden coupling, gate it behind a feature flag for a release.

---

**End of audit.** Cross-references: see `docs/architecture/plugin-architecture.md` for the existing `IBowireProtocol` / `IBowireUiExtension` patterns this proposal mirrors, and `docs/architecture/packages.md` for the bundle composition story.
