# Bowire manual walkthrough

> Step-by-step UI/UX walkthrough used to **verify every surface still works** after a feature push and to **drive Playwright test coverage**. Each phase is a self-contained scenario; each step has an *Expected* outcome the operator checks against the live UI. When a step fails or the UX doesn't match, the phase becomes a bug ticket (or a fix on the spot if small) and the eventual Playwright test guards the regression.

## How to use this document

1. **Pre-flight**: Tool running on `http://localhost:5180/`, browser open, DevTools console handy (`localStorage.clear()` between phases that need a fresh slate).
2. **Walk a phase** top-to-bottom, checking each *Expected*. Tick `[x]` on pass; leave `[ ]` and note the failure mode for fails.
3. **Fix or file** — small UX gaps get fixed inline; bigger ones become tickets.
4. **Capture as Playwright** — once a phase reads green end-to-end, fold the steps into `tests/Kuestenlogik.Bowire.E2E.Tests/<phase>.spec.ts` so future regressions trip on CI, not in someone's browser.

Status convention per step: `[ ]` not yet verified · `[x]` passes · `[!]` known bug, ticket linked · `[~]` partially passes.

---

## Phase 1 — Empty state (first run / no workspace)

**Setup:** `localStorage.clear()` + reload.

### What should be visible

- [ ] Topbar carries the **B**-logo + brand, env selector (says "Workspace defaults" or similar default), command palette, **Undo / Redo / Trash** trio (#296), theme toggle, help, settings cog. **No** workspace chip (no workspace exists).
- [ ] Left rail shows the always-on set: **Home** (active by default), Discover, **Compose**, Workspaces. Disabled / optional rails (Recordings, Mocks, Flows, Proxy, Benchmarks, Security) MAY also appear unchecked-but-visible.
- [ ] Sidebar shows nothing rail-mode-specific (Home rail has `sidebar.kind: 'none'`).
- [ ] Main pane renders the **"Create your first workspace"** empty card:
  - icon (layers)
  - headline `Create your first workspace`
  - body text explaining what a workspace is
  - primary action button `Create workspace`
- [ ] The Undo + Redo buttons render in their disabled state (greyed, `aria-disabled="true"`) — there is nothing on the action-log stack yet. The Trash icon shows no badge (aggregate count == 0).

### Canonical rail welcome-card shape (#301)

Every rail's "home / welcome / no-selection" surface MUST render the shared `renderEmptyCard({ icon, headline, body, actions })` helper, wrapped in the canonical pane shell so the visual rhythm is identical across rails. Future rails copy this shape; existing rails (Discover, Recordings, Collections, Mocks, Flows, Proxy, Benchmarks, Workspaces overview) already conform — diverging from this is a regression.

**Markup contract**

```js
var main = el('div', { id: 'bowire-main-<rail>', className: 'bowire-main bowire-main-<rail>' });
var emptyWrap = el('div', { className: 'bowire-main-pad' });
emptyWrap.appendChild(renderEmptyCard({
    icon: '<svg-id>',          // e.g. 'recording', 'folder', 'chart', 'layers'
    headline: '<short title>', // e.g. 'No recordings yet' / 'Pick a recording'
    body: '<one-paragraph>',   // what the rail is for + how to start
    actions: [                 // 0..N — first one is { primary: true }
        { label: 'Primary CTA', primary: true, onClick: function () { /* ... */ } },
        { label: 'Secondary',   onClick: function () { /* ... */ } }
    ]
}));
main.appendChild(emptyWrap);
return main;
```

**Components rendered by `renderEmptyCard`** (`helpers.js` → `.bowire-empty-card`):

- `.bowire-empty-card-icon` — accent-tinted 22px svg glyph that anchors the rail's identity.
- `.bowire-empty-card-headline` — `<h3>`, 15px / 600.
- `.bowire-empty-card-body` — `<p>`, 13px / secondary text, max 560px.
- `.bowire-empty-card-actions` — flex row; primary CTA fills accent, secondaries pick up the surface fill.

**Walkthrough check**: open each rail in turn (Discover → Compose → Recordings → Mocks → Flows → Proxy → Benchmarks → Security → Workspaces). The icon + headline + body + CTA cluster should align at the same gutter on every rail and read with the same typography. If a rail's welcome card looks different — typography off, padding off, header glyph instead of inline icon, custom paragraph instead of `.bowire-empty-card-body` — that's the #301 class of regression: refactor it back to this shape.

### Actions to try

1. [ ] Click `Create workspace` → Create-workspace dialog opens. Tested in Phase 2.
2. [ ] Click another rail (e.g. Discover) → it should switch (since the force-home trap was retired).
   - **Expected**: rail-btn highlights the new mode, main pane renders the rail's no-workspace empty state.
   - **Watch for**: if rail-click does nothing, force-home rule may have crept back.
3. [ ] Click topbar **B**-logo → opens the app-drawer with rail-mode shortcuts.
4. [ ] Open command palette (Ctrl+K) → search for `compose` → `Compose new request` should appear.
5. [ ] Settings cog → Settings dialog opens, sidebar tree has My preferences (General / Rail modes / Shortcuts / Data / Assistant), Plugins (with sub-entries for every installed plugin), This project (Workspace pointer).
6. [ ] **#296** — Topbar Undo / Redo / Trash trio:
   - Hover Undo → tooltip reads `Nothing to undo (Ctrl/Cmd+Z)` while the stack is empty; after a reversible action (delete a recording, a collection, etc.) the tooltip flips to `Undo: <action title> (Ctrl/Cmd+Z)` and the button enables.
   - Same pattern for Redo after an Undo: `Redo: <action title> (Ctrl/Cmd+Shift+Z)`.
   - Click the Trash icon → modal-style **Trash** drawer opens, headlined `Trash — N items`. Per-bucket sections (`Recordings`, `Collections`, `Recently closed tabs`) expand on click; each row carries `Restore` + `Delete forever` + a `Deleted X days ago — will be purged in (30-X) days` TTL note. Footer actions: `Empty trash` (confirm), `Restore all` (confirm), `Cancel`.
   - The `Workspaces` row in the drawer renders as `(0) — Soft-delete arrives with #194` until that ticket lands.
   - Esc / backdrop click closes the drawer.

### Playwright test outline

- `phase1-empty-state.spec.ts`:
  - Setup: navigate, clear storage, reload.
  - Assert: empty-state headline + create-workspace button visible.
  - Assert: clicking each toggleable rail does NOT snap back to home.
  - Assert: Settings → Rail modes lists Home/Discover/Workspaces as locked + others as toggleable.

---

## Phase 2 — Create workspace

**Setup:** continue from Phase 1 (no workspaces yet).

### Create-workspace dialog

1. [ ] Click `Create workspace` (or `+` New from the topbar's workspace chip menu once one exists).
2. [ ] Dialog opens with three regions:
   - **Header**: title `Create workspace`, close X
   - **Form**: Name input (focus by default), color picker, description (optional)
   - **Templates list**: built-in templates (Empty, REST API testing, gRPC services, Mock server build, Multi-protocol smoke test) + `Your templates` section (if any user-templates saved).
3. [ ] Each built-in template row shows: icon + label + description.
4. [ ] Template hover-state shows clickable cursor.
5. [ ] Validation: clicking `Create` with empty name disables the button (or toasts).

### Workspace seeded from a template

1. [ ] Type `Petstore Test`, pick the **REST API testing** template.
2. [ ] Click `Create` → dialog closes, workspace switcher animates, new workspace becomes active.
3. [ ] **Expected** after seeding:
   - Topbar's workspace chip shows `Petstore Test` with the chosen color.
   - Sources list contains `https://petstore.swagger.io/v2` (or `petstore3.swagger.io/api/v3` per the template's current URL — check `workspace-templates.js`).
   - Discovery fires automatically — Pet / Store / User services land in the Discover sidebar tree.
   - A starter collection (`Petstore quick start` or similar) lands under Collections.
4. [ ] Pages reload? — current implementation does a `window.location.reload()` so the freshly-templated localStorage hydrates the in-memory state. The brief flash should land on Discover.

### Empty template path

1. [ ] Create a second workspace `Empty Test` using the **Empty** template.
2. [ ] Switch into it via the topbar chip.
3. [ ] **Expected**: no URLs, no services, no collections — clean slate. Sources sidebar shows the empty state ("Add a URL to discover services" or similar).

### Playwright test outline

- `phase2-create-workspace.spec.ts`:
  - REST template: assert serverUrls[0] === petstore URL, services list > 0 after discovery.
  - Empty template: assert workspaces.length === 2 in localStorage, second one has no sources / no services.
  - Validation: empty-name `Create` click does NOT close the dialog.

---

## Phase 3 — Workspace management

**Setup:** two workspaces (from Phase 2).

### All workspaces overview

1. [ ] Click rail **Workspaces** icon → sidebar shows the workspaces tree (one node per workspace, expandable to Sources / Environments / Collections / Recordings / Settings).
2. [ ] Three paths to the overview list:
   - Click sidebar title `WORKSPACES` (the heading itself is a button).
   - Sidebar toolbar overflow `⋯` → `Show all workspaces`.
   - Open any workspace's settings detail → header action `All workspaces`.
3. [ ] **Expected** overview pane:
   - Header glyph + `Workspaces (2)`.
   - List of workspace rows: glyph (workspace color) + name + env-count meta + active-checkmark + hover-revealed tools.

### Switch active workspace

1. [ ] On the overview, click the `✓` next to the non-active workspace → switches. Topbar chip updates.

### Edit / rename

1. [ ] On the overview, hover an inactive workspace row → tools cluster reveals (Rename + Save-as-template + Delete).
2. [ ] Click rename pencil → inline prompt or modal → enter new name → confirm → row updates.
3. [ ] Click the workspace name itself → opens the settings detail page.

### Settings detail

1. [ ] Header shows: glyph + workspace name + Active badge OR Switch button.
2. [ ] Header actions cluster: **All workspaces** · Save now (only when dirty) · Duplicate… · Save as template… (only when active).
3. [ ] Tabs: General · Variables · Secrets · Plugin pins · Sources URL list · Recently deleted.
4. [ ] Each tab loads without errors; per-tab buttons (e.g. + add var, + add secret) work.

### Save-as-template

1. [ ] On the active workspace's settings page, click `Save as template…` → prompt for template name → confirm.
2. [ ] Toast: `Saved "<name>" — available in the next create-workspace dialog.`
3. [ ] Open Create-workspace dialog → `Your templates` section now shows the new template with hover-delete.
4. [ ] Create a workspace from the user-template → verify it's seeded identically to the source workspace.

### Duplicate

1. [ ] Settings detail → `Duplicate…` → prompt for name → confirm → new workspace appears in the list, sources/envs/collections/recordings/flows/pins all copied.

### Delete

1. [ ] Overview → hover row → delete trash icon → confirm dialog → workspace removed.
2. [ ] Delete the LAST workspace → switches to "no workspace" state.
3. [ ] **Expected**: every rail icon stays clickable (force-home retired). Operator can browse Discover/Settings even without a workspace.

### Playwright test outline

- `phase3-workspace-management.spec.ts`:
  - Create 2 workspaces, navigate Overview via all three paths.
  - Switch active, assert topbar chip updates.
  - Rename, assert sidebar row reflects new name.
  - Duplicate, assert workspaces.length === 3 in localStorage.
  - Save-as-template, re-open Create dialog, assert Your-Templates section non-empty.
  - Delete last workspace, click Discover rail, assert main pane renders Discover empty state (NOT Home).

---

## Phase 4 — Sources (URLs + Schema)

**Setup:** one workspace, active.

### Add a URL

1. [ ] Sidebar workspace tree → expand `Sources` → click → main pane shows the Sources editor.
2. [ ] Click `+ Add URL` (or the topbar connection pill → Add URL flow).
3. [ ] Type a URL (e.g. `https://petstore.swagger.io/v2`) → submit.
4. [ ] **Expected**: URL row added to the list, connection pill shows discovering state, then settles to connected with service-count badge.

### Schema upload

1. [ ] Sources editor → `Upload schema file` → pick a `.proto`, `openapi.yaml`, or `.graphql` file.
2. [ ] Toast: file accepted, services added to Discover sidebar.
3. [ ] **Expected**: uploaded schema's services merge into the discovered tree alongside any URL-discovered ones.

### URL editing / removal

1. [ ] Per-URL row hover → tools cluster (edit, refresh, remove).
2. [ ] Click refresh → discovery re-runs.
3. [ ] Remove URL → toast + undo.

### Test the URL

1. [ ] Rail → Discover → pick any discovered method → method pane opens.
2. [ ] Tabs: Payload (Form / Body sub-tabs for REST) · Metadata · Schema · Code · Scripts · Tests · History.
3. [ ] Click `Execute` → response renders in the right pane.
4. [ ] Console drawer at bottom logs REQUEST + RESPONSE entries with status + duration.

### Playwright test outline

- `phase4-sources.spec.ts`:
  - Add petstore URL → assert services.length > 0 after discovery settles.
  - Pick a Pet/getPetById method → Execute → assert response pane has `Browney` (or a 2xx).
  - Refresh URL → assert discovery re-ran (timestamp).
  - Remove URL → assert sources list empty.

---

## Phase 5 — Environments + Variables

**Setup:** one workspace, ≥1 URL discovered.

### Workspace-scope envs

1. [ ] Sidebar tree → Environments → click → main pane shows env editor.
2. [ ] `+ New env` → name input → submit.
3. [ ] Add a few KV pairs (e.g. `baseUrl=https://petstore3.swagger.io`, `apiKey=demo`).
4. [ ] Secrets tab → add a secret (e.g. `token=secret123`). Secrets are masked.
5. [ ] Switch active env via the topbar env selector → method invocations now resolve `${baseUrl}` / `${apiKey}` etc.

### Variable resolution

1. [ ] Discover → pick a method → request body or metadata uses `${baseUrl}/path/123` → click Execute.
2. [ ] **Expected**: the actual call hits `https://petstore3.swagger.io/path/123`.
3. [ ] Console request log shows the substituted value (or the un-substituted form depending on log policy).

### Global vars (cross-workspace)

1. [ ] Settings → My preferences → Variables (or similar) — global vars editor.
2. [ ] Add a global var → reference it from a method body.
3. [ ] **Expected**: global vars resolve when workspace env doesn't override.

### Playwright test outline

- `phase5-environments.spec.ts`:
  - Create env, set baseUrl, switch active.
  - Build a method body referencing `${baseUrl}` → execute → assert outbound URL contains the resolved value.

---

## Phase 6 — Discovered method invocation

Already partly covered in Phase 4 but expand:

1. [ ] Method header: protocol breadcrumb (icon + name + svc) · method name · description · favorite star · presets picker · `+ Add to…` menu · proto-icon · verb badge · path label.
2. [ ] Request pane tabs: Payload (Form / Body) · Metadata · Schema · Code · Scripts · Tests · History.
3. [ ] Form mode: schema-driven field editor; required-field validation runs before invoke.
4. [ ] Body mode: raw JSON / text editor with JSON validator.
5. [ ] Execute split-button: primary (Execute) + caret menu (Run as benchmark · Run with preset · As new request).
6. [ ] Response pane tabs: Response · Response Metadata · Test results (when Unary + tests exist) · Diff (when ≥2 snapshots).
7. [ ] Action bar at bottom: status indicators, duration, request/response sizes.

### Playwright test outline

- `phase6-invoke.spec.ts`:
  - Form mode: fill required field, execute, assert response.
  - Body mode: paste JSON, execute, assert response.
  - `+ Add to… → Collection` → assert collection grows by 1 item.

---

## Phase 7 — Ad-hoc / freeform requests

### Compose new request (self-contained REST)

1. [ ] Home rail → Quick Actions → `Compose new request`.
2. [ ] Freeform builder opens with empty URL field + REST protocol selected + GET verb default.
3. [ ] Type a full URL (e.g. `https://petstore3.swagger.io/api/v3/pet/5`) → click `GET` → click `Execute`.
4. [ ] Response pane shows the JSON; console logs `GET https://…` REQUEST + RESPONSE entries.

### POST / PUT / DELETE / PATCH with body

1. [ ] Switch verb → for POST/PUT/PATCH, paste a JSON body in Payload tab → Execute → assert request hit the URL with the body.

### Metadata (custom headers)

1. [ ] Switch to Metadata tab → add `Authorization: Bearer xxx` row → execute → server received the header (check response Echo or DevTools network).

### As new request (clone from discovered)

1. [ ] On a discovered method's Execute caret → `As new request…` → freeform opens pre-filled.
2. [ ] Cancel tooltip reads `cloned from <svc>/<method>`.
3. [ ] Tab-switch and back → clone persists.
4. [ ] Close tab → neighbor tab's original method renders.

### Save to a collection

1. [ ] Freeform builder header → `+ Add to…` → pick a collection → toast + entry added.

### Other protocols

1. [ ] Switch protocol to gRPC → header shows service + method inputs (not the REST verb bar).
2. [ ] Similar smoke for GraphQL, MQTT, WebSocket — verify each protocol's freeform shape matches its native call shape.

### Playwright test outline

- `phase7-freeform.spec.ts`:
  - Compose REST → enter URL + GET → execute → assert response.
  - As-new-request from discovered → tab-detour → assert clone preserved.
  - Save to collection → assert collection item shape includes `urlMode: 'inline'`.

---

## Phase 8 — Collections

Collections + Presets are managed from the **Compose rail's side panel**; the standalone Collections rail is **off by default** as of #304. Run the side-panel checks first, then opt into the standalone rail to exercise its surfaces.

### Side panel (default surface)

1. [ ] Compose rail → expand the side panel from the gutter handle → assert both `Collections` and `Presets` tabs render.
2. [ ] Collections tab → empty state when no collections.
3. [ ] `+ New collection` (inside the side panel) → name → create.
4. [ ] Add items via `+ Add to…` on a discovered method, request-builder `Save to collection`, or recording-step `Open in Compose`.
5. [ ] Collection detail (inline in the panel): list of items, per-item replay button, drag-to-reorder.
6. [ ] Run-all → executes every item in order, shows pass/fail per row.

### Standalone rail (opt-in)

1. [ ] Settings → Rail modes → assert `Collections` row renders unchecked (default-off).
2. [ ] No standalone Collections rail icon in the strip; workspace-tree `Collections` node hidden under each workspace; per-method `C` pill suppressed in the Discover tree.
3. [ ] Toggle `Collections` on in Settings → rail-strip icon, workspace-tree node, and per-method `C` pill all reappear without reload.
4. [ ] Standalone rail still works: Collections rail → empty state, `+ New collection`, run-all, drag-to-reorder.
5. [ ] Toggle off again → tree node + strip icon disappear; existing collections data preserved (visible again via the Compose side panel).

### Playwright test outline

- `phase8-collections.spec.ts`:
  - Default render: Collections rail icon hidden, Compose side panel renders Collections tab.
  - Create collection from side panel, add 3 items, run-all, assert all-pass.
  - Toggle standalone rail on via Settings → re-assert strip icon + tree node visible.

---

## Phase 9 — Recordings

1. [ ] Recordings rail.
2. [ ] `Start recording` → invoke a few discovered methods → stop.
3. [ ] Recording detail: per-step list with status + response.
4. [ ] Replay-all → re-runs each step.
5. [ ] Export: Bowire JSON · HAR.

---

## Phase 10 — Mocks

1. [ ] Mocks rail.
2. [ ] Build mock from recording (`Use as mock` on a recording).
3. [ ] Start mock server → status indicator on, port shown.
4. [ ] Invoke against the mock port → response matches the recorded step.

---

## Phase 11 — Flows

1. [ ] Flows rail → empty state.
2. [ ] `+ New flow` → visual canvas.
3. [ ] Drop nodes (HTTP request, JS transform, branch).
4. [ ] Wire edges, run → results panel.

---

## Phase 12 — Benchmarks

1. [ ] Benchmarks rail.
2. [ ] Method `+ Add to… → Benchmark envelope` → envelope picker → add target.
3. [ ] Envelope → set N + concurrency → Run.
4. [ ] Results: p50/p95/p99 latency, status histogram, throughput.

---

## Phase 13 — Security

1. [ ] Security rail (only when `Kuestenlogik.Bowire.Ai` installed).
2. [ ] Threat model: tier toggle, ranked endpoints.
3. [ ] Fuzz: schema-aware values, run against selected method.

---

## Phase 14 — Settings

### My preferences

- [ ] General: theme, identity cue, hints, autosave toggle.
- [ ] Rail modes (#248 Phase 1): Always-available list (Home, Discover, Workspaces — locked) + Toggleable list (each can be hidden).
- [ ] Shortcuts: read-only keyboard cheat sheet.
- [ ] Data: clear localStorage, export, reset hints.
- [ ] Assistant: AI provider config (OpenAI / Anthropic / local), API key, model picker.

### Plugins

- [ ] Plugins overview: every installed plugin with enable/disable toggle.
- [ ] Per-plugin detail (clickable in tree): enable toggle + plugin-specific settings (most plugins show "No configurable settings").

### This project

- [ ] Workspace pointer → opens the active workspace's settings detail.

---

## Phase 15 — Smoke regression

After each release, walk through phases 1-7 end-to-end in a single browser session, then phases 8-14 individually with fresh state.

When CI runs the Playwright suite, this manual walkthrough should still pass identically — Playwright covers the obvious paths, the manual walk catches the per-render polish (animations, hover states, tooltips, transition jank).

---

## Open questions / known gaps (to address as we walk)

- [ ] Phase 1: "All workspaces" button click sometimes routes to settings instead of overview — Playwright probe sees the button but the post-click state shows no overview list. To investigate during the walkthrough.
- [ ] Phase 2: Workspace create dialog's user-template rows lack tooltip clarification for "compose" vs "cloned-from-source" lineage.
- [ ] Phase 4: Schema upload's accepted file-type list isn't documented in the UI — operator drops a non-schema file → silent reject?
- [ ] Phase 7: gRPC freeform requires a `.proto` file or server reflection. UI should hint at this when method-tree stays empty.
- [ ] Phase 11: Flows visual editor — node palette discoverability untested.
- [ ] Phase 13: Security rail availability depends on AI package — empty-state hint shown when missing?
