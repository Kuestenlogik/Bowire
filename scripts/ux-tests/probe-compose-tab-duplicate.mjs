// Regression probe: the Compose tab context-menu 'Duplicate tab' action.
//
// The v2.1 release notes promise: "A Compose tab supports right-click →
// Duplicate tab so you can fan out a request without losing the original."
// This probe walks that flow end-to-end against the running Tool at
// http://localhost:5180 and asserts:
//
//   1. The context menu on a Compose tab exposes a 'Duplicate tab' item.
//   2. Clicking it creates a new tab (visible tab count grows by 1).
//   3. The clone is inserted immediately after the source in the tab
//      strip and becomes the new active tab.
//   4. The clone carries a deep copy of the source's request state —
//      verified by inspecting the persisted bowire_ws_<id>_compose_tabs
//      blob (composeTabs lives inside the compose-rail closure, and the
//      persist path mirrors every mutation to localStorage, so it's our
//      read window).
//   5. Mutating the clone's URL does NOT bleed back into the source
//      (deep-copy guarantee — the two tabs must not share references).
//
// State-seed strategy: we drive the URL input (a plain <input>) with a
// Playwright fill(), which fires the onInput handler → mutates the
// freeformRequest → persists. This side-steps the fact that neither
// freeformRequest nor composeTabs are window globals; both live inside
// closures.
//
// Run with: node scripts/ux-tests/probe-compose-tab-duplicate.mjs
// Exit code 0 = all assertions pass, 1 = at least one failure.

import { chromium } from '@playwright/test';

const WORKSPACE_ID = 'probe_dup';
const STORAGE_KEY = `bowire_ws_${WORKSPACE_ID}_compose_tabs`;

const results = [];
function check(label, pass, detail) {
    results.push({ label, pass, detail: detail || '' });
}

const browser = await chromium.launch({ headless: true, args: ['--ignore-certificate-errors'] });
const page = await browser.newPage({ ignoreHTTPSErrors: true, viewport: { width: 1400, height: 900 } });

// Seed a workspace + land the operator directly on the Compose rail so
// the tab strip is guaranteed to paint on the first render.
await page.goto('http://localhost:5180/', { waitUntil: 'domcontentloaded' });
await page.evaluate((wsId) => {
    localStorage.setItem('bowire_workspaces', JSON.stringify([{
        id: wsId, name: 'Duplicate probe', color: 'sky', createdAt: 1_700_000_000_000,
    }]));
    localStorage.setItem('bowire_active_workspace_id', wsId);
    localStorage.setItem('bowire_rail_mode', 'compose');
    // Nuke any stale compose-tab persistence from a previous run.
    localStorage.removeItem(`bowire_ws_${wsId}_compose_tabs`);
}, WORKSPACE_ID);
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
// The Compose rail paints its pinned '+' button regardless of whether
// any tabs are open. Click it if we don't already have a live tab —
// this guarantees a well-defined starting shape (exactly one tab).
await page.waitForSelector('#bowire-compose-tab-pinned', { timeout: 10000 });
if (await page.locator('.bowire-compose-tab').count() === 0) {
    await page.locator('#bowire-compose-tab-pinned').click();
    await page.waitForSelector('.bowire-compose-tab', { timeout: 5000 });
}
await page.waitForSelector('#bowire-request-builder-url-input', { timeout: 5000 });

// Small helper: read the persisted compose-tabs blob out of localStorage.
async function readPersisted() {
    return await page.evaluate((key) => {
        const raw = localStorage.getItem(key);
        return raw ? JSON.parse(raw) : null;
    }, STORAGE_KEY);
}

// Seed a distinctive URL on the currently-active tab. fill() dispatches
// an input event which the URL input's onInput handler catches and
// writes back onto the live freeformRequest.
const SEED_URL = 'https://example.test/probe/duplicate';
const MUTATED_URL = 'https://mutated.test/clone-only';
await page.locator('#bowire-request-builder-url-input').fill(SEED_URL);

// Snapshot pre-duplicate state via the DOM.
const beforeTabIds = await page.locator('.bowire-compose-tab').evaluateAll(
    (els) => els.map((el) => el.getAttribute('data-tab-id')));
const activeIdBefore = await page.locator('.bowire-compose-tab.active').first().getAttribute('data-tab-id');
check('start: exactly one compose tab present', beforeTabIds.length === 1,
    `ids=${JSON.stringify(beforeTabIds)}`);
check('start: active tab id resolved', !!activeIdBefore, `activeId=${activeIdBefore}`);
check('start: URL input reflects seeded URL',
    await page.locator('#bowire-request-builder-url-input').inputValue() === SEED_URL,
    'seeded=' + SEED_URL);

// Right-click the active tab. Playwright's click({ button: 'right' })
// fires a real contextmenu event, which the compose-rail handler wires up.
const activeTabSel = `#bowire-compose-tab-${activeIdBefore}`;
await page.locator(activeTabSel).click({ button: 'right' });

// Verify the menu paints + carries a Duplicate tab entry.
const menuItems = await page.locator('.bowire-context-menu .bowire-context-menu-item-label').allTextContents();
check('menu: Duplicate tab entry present', menuItems.includes('Duplicate tab'),
    'items=' + JSON.stringify(menuItems));

// Click the Duplicate tab entry. Scope to the context menu so we don't
// accidentally hit a docs snippet or anywhere else the string appears.
await page.locator('.bowire-context-menu .bowire-context-menu-item').filter({ hasText: 'Duplicate tab' }).click();
// Give render() a beat to flush and persistDesignTabs to run.
await page.waitForTimeout(200);

// Snapshot post-duplicate state.
const afterTabIds = await page.locator('.bowire-compose-tab').evaluateAll(
    (els) => els.map((el) => el.getAttribute('data-tab-id')));
const activeIdAfter = await page.locator('.bowire-compose-tab.active').first().getAttribute('data-tab-id');
const persistedAfter = await readPersisted();

check('duplicate: DOM tab count grew by 1',
    afterTabIds.length === beforeTabIds.length + 1,
    `${beforeTabIds.length} -> ${afterTabIds.length}`);

const srcIdxAfter = afterTabIds.indexOf(activeIdBefore);
const cloneIdxAfter = srcIdxAfter + 1;
const cloneId = afterTabIds[cloneIdxAfter];

check('duplicate: clone inserted immediately after source',
    !!cloneId && cloneId !== activeIdBefore,
    `srcIdx=${srcIdxAfter} cloneIdx=${cloneIdxAfter} cloneId=${cloneId}`);
check('duplicate: clone becomes the active tab',
    activeIdAfter === cloneId,
    `activeId=${activeIdAfter} cloneId=${cloneId}`);

// Read the persisted request bodies so we can compare source vs clone.
const persistedTabsById = {};
for (const t of (persistedAfter && persistedAfter.tabs) || []) {
    persistedTabsById[t.id] = t.request || {};
}
const srcReq = persistedTabsById[activeIdBefore] || {};
const cloneReq = persistedTabsById[cloneId] || {};
check('duplicate: source URL persisted correctly',
    srcReq.serverUrl === SEED_URL,
    `src=${srcReq.serverUrl}`);
check('duplicate: clone URL matches source', cloneReq.serverUrl === SEED_URL,
    `clone=${cloneReq.serverUrl}`);
// Sanity: since the tab is active, the URL input should read the clone's URL.
const urlOnCloneActive = await page.locator('#bowire-request-builder-url-input').inputValue();
check('duplicate: URL input on active clone shows seeded URL',
    urlOnCloneActive === SEED_URL,
    `input=${urlOnCloneActive}`);

// Mutate the clone via the URL input (clone is active). Then switch back
// to source and verify source's URL input still reads the seeded value —
// deep-copy guarantee. If the two tabs shared a request object, the
// source's URL would have flipped to MUTATED_URL too.
await page.locator('#bowire-request-builder-url-input').fill(MUTATED_URL);
// Confirm the URL input on the clone (still active) now reads MUTATED_URL.
const urlOnCloneAfterFill = await page.locator('#bowire-request-builder-url-input').inputValue();
check('deep-copy: clone URL input reflects mutation before switch',
    urlOnCloneAfterFill === MUTATED_URL,
    `clone-input-after-fill=${urlOnCloneAfterFill}`);
// Snapshot the URL input on source by clicking source tab.
await page.locator(`#bowire-compose-tab-${activeIdBefore}`).click();
await page.waitForTimeout(150);
const urlOnSourceAfterMut = await page.locator('#bowire-request-builder-url-input').inputValue();
check('deep-copy: source URL unchanged after clone edit (DOM)',
    urlOnSourceAfterMut === SEED_URL,
    `source-input=${urlOnSourceAfterMut}`);

// And confirm via the persisted blob for good measure.
await page.waitForTimeout(120);
const persistedAfterMut = await readPersisted();
const byIdAfterMut = {};
for (const t of (persistedAfterMut && persistedAfterMut.tabs) || []) {
    byIdAfterMut[t.id] = t.request || {};
}
check('deep-copy: source URL unchanged after clone edit (persisted)',
    (byIdAfterMut[activeIdBefore] || {}).serverUrl === SEED_URL,
    `src=${(byIdAfterMut[activeIdBefore] || {}).serverUrl} clone=${(byIdAfterMut[cloneId] || {}).serverUrl}`);
check('deep-copy: clone URL reflects mutation (persisted)',
    (byIdAfterMut[cloneId] || {}).serverUrl === MUTATED_URL,
    `clone=${(byIdAfterMut[cloneId] || {}).serverUrl}`);

// Report.
const passed = results.filter(r => r.pass).length;
const total = results.length;
console.log(`\n--- probe-compose-tab-duplicate: ${passed}/${total} assertions passed ---\n`);
for (const r of results) {
    console.log(`  ${r.pass ? 'PASS' : 'FAIL'}  ${r.label}${r.detail ? '  (' + r.detail + ')' : ''}`);
}
await browser.close();
if (passed !== total) process.exit(1);
