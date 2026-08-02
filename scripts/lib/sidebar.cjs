/**
 * Shared Playwright helpers for driving the workbench's Discover sidebar.
 *
 * WHY THIS FILE EXISTS (#551)
 * ---------------------------
 * Until #551 the sidebar built a `.bowire-method-item` row for every
 * method of every service and merely hid the collapsed ones with CSS.
 * The screenshot / UX-probe scripts leaned on that: they waited for
 * `.bowire-method-item` as the "catalogue has loaded" signal and clicked
 * rows that were attached-but-invisible.
 *
 * `buildServiceMethodList()` in wwwroot/js/render-sidebar.js now returns
 * an EMPTY `.bowire-method-list` for a collapsed group — the rows simply
 * do not exist in the DOM. So:
 *
 *   - the catalogue-loaded signal is now `.bowire-service-group`,
 *     not `.bowire-method-item`;
 *   - every group must be expanded BEFORE any wait on / interaction
 *     with a `.bowire-method-item`.
 *
 * Small catalogues still appear to work by accident: api.js auto-expands
 * everything when the catalogue has <= 5 services and nothing is
 * persisted under `bowire_expanded_services`. A fresh Playwright profile
 * is empty, so <= 5 services expand themselves. Do not rely on it —
 * bigger catalogues (and any run against a persisted profile) do not.
 *
 * DOM CONTRACT (render-sidebar.js, verified against the source)
 * -------------------------------------------------------------
 *   .bowire-service-group                 one per service
 *     > .bowire-service-header            clickable, toggles the group
 *         .bowire-service-chevron         carries `expanded` when open
 *     > .bowire-method-list               carries `expanded` when open;
 *                                         holds .bowire-method-item rows
 *                                         ONLY while expanded
 *
 * A service group carries NO `collapsed` class, and there is no
 * `.bowire-service-group-header` class at all — the old
 * `.bowire-service-group.collapsed .bowire-service-group-header`
 * selector matched nothing and failed silently for as long as it
 * existed. Do not reintroduce it. (The only `collapsed` class in the
 * sidebar sits on `.bowire-source-panel-body`, a different level of the
 * tree.)
 *
 * The favourites group (`.bowire-service-group.bowire-favorites-group`)
 * is deliberately NOT a toggle: it holds `.bowire-method-item` rows
 * directly and has no header/chevron, so the collapsed-header selector
 * below never picks it up.
 *
 * KNOWN LIMIT: a service group nested inside a COLLAPSED source panel
 * (`.bowire-source-panel-body.collapsed`) is in the DOM but not
 * clickable. Source panels default to open, so this does not bite in
 * practice; if it ever does, expandAllServices() bails out after three
 * fruitless rounds and reports a non-zero `stillCollapsed` rather than
 * spinning.
 *
 * MODULE FORMAT
 * -------------
 * This is the ONE canonical copy — do not paste these functions into
 * individual scripts. It is authored as CommonJS on purpose so both
 * consumer families can load it without a bridge or a loader dance:
 *
 *   scripts/screenshots/*.js   (CJS)  const sidebar = require('../lib/sidebar.cjs');
 *   scripts/ux-tests/*.mjs     (ESM)  import { openCatalogue } from '../lib/sidebar.cjs';
 *
 * Node resolves the ESM named imports through cjs-module-lexer, which
 * needs the single static `module.exports = { ... }` assignment at the
 * bottom of this file. If you switch to conditional or computed
 * exports, the `.mjs` importers break loudly at import time — keep the
 * static form.
 */

'use strict';

/** One node per discovered service. The catalogue-loaded signal. */
const SERVICE_GROUP_SELECTOR = '.bowire-service-group';

/** Clickable row that toggles a service group open/shut. */
const SERVICE_HEADER_SELECTOR = '.bowire-service-header';

/** A method row. Only exists while its owning group is expanded. */
const METHOD_ITEM_SELECTOR = '.bowire-method-item';

/**
 * Header of a group whose chevron lacks the `expanded` marker, i.e. a
 * group that is currently shut. Re-evaluated on every use, so it always
 * reflects the live DOM after morphdom re-renders the sidebar.
 */
const COLLAPSED_SERVICE_HEADER_SELECTOR =
    `${SERVICE_GROUP_SELECTOR} > ${SERVICE_HEADER_SELECTOR}:has(.bowire-service-chevron:not(.expanded))`;

/**
 * Waits until the service catalogue has rendered.
 *
 * Use this instead of waiting on `.bowire-method-item`: a catalogue
 * whose groups are all collapsed renders zero method rows, so the old
 * signal never fires.
 *
 * @param {import('@playwright/test').Page} page
 * @param {{ timeout?: number }} [options]
 * @returns {Promise<number>} number of service groups rendered
 */
async function waitForCatalogue(page, options) {
    const opts = options || {};
    const timeout = opts.timeout === undefined ? 30000 : opts.timeout;
    await page.waitForSelector(SERVICE_GROUP_SELECTOR, { state: 'attached', timeout });
    return page.locator(SERVICE_GROUP_SELECTOR).count();
}

/**
 * Expands every collapsed service group by clicking its header — the
 * same path a user takes, so the persisted `bowire_expanded_services`
 * state ends up exactly as a real session would leave it.
 *
 * Each click re-renders the sidebar (morphdom), which is why the
 * collapsed set is re-queried between clicks rather than iterated from
 * a stale snapshot. Idempotent: a group that is already open never
 * matches the selector, so it is never clicked shut again.
 *
 * @param {import('@playwright/test').Page} page
 * @param {{ stepMs?: number, settleMs?: number, clickTimeout?: number, maxClicks?: number }} [options]
 * @returns {Promise<{ clicks: number, stillCollapsed: number }>}
 */
async function expandAllServices(page, options) {
    const opts = options || {};
    const stepMs = opts.stepMs === undefined ? 120 : opts.stepMs;
    const settleMs = opts.settleMs === undefined ? 400 : opts.settleMs;
    const clickTimeout = opts.clickTimeout === undefined ? 5000 : opts.clickTimeout;
    const maxClicks = opts.maxClicks === undefined ? 200 : opts.maxClicks;

    const collapsed = page.locator(COLLAPSED_SERVICE_HEADER_SELECTOR);
    let clicks = 0;
    // Consecutive rounds that failed to shrink the collapsed set. Bounds
    // the loop when a header refuses to react (detached mid-render,
    // covered by an overlay, ...) instead of spinning to maxClicks.
    let stalled = 0;

    while (clicks < maxClicks) {
        const before = await collapsed.count();
        if (before === 0) break;
        // Always click the first one: the set shrinks under us as the
        // sidebar re-renders, so index-based iteration would skip rows.
        await collapsed.first().click({ timeout: clickTimeout }).catch(() => { /* re-render race */ });
        clicks++;
        await page.waitForTimeout(stepMs);
        const after = await collapsed.count();
        if (after >= before) {
            stalled++;
            if (stalled >= 3) break;
        } else {
            stalled = 0;
        }
    }

    await page.waitForTimeout(settleMs);
    return { clicks, stillCollapsed: await collapsed.count() };
}

/**
 * The one call most scripts want: wait for the catalogue, expand every
 * group, then confirm method rows actually exist.
 *
 * Pass `requireMethods: false` for surfaces that only need the tree
 * (a sidebar clip, a group-header shot) or for catalogues that
 * legitimately expose services without methods.
 *
 * @param {import('@playwright/test').Page} page
 * @param {{ timeout?: number, methodTimeout?: number, requireMethods?: boolean,
 *           stepMs?: number, settleMs?: number, clickTimeout?: number, maxClicks?: number }} [options]
 * @returns {Promise<{ groups: number, clicks: number, stillCollapsed: number }>}
 */
async function openCatalogue(page, options) {
    const opts = options || {};
    const groups = await waitForCatalogue(page, { timeout: opts.timeout });
    const expanded = await expandAllServices(page, opts);
    if (opts.requireMethods !== false) {
        const methodTimeout = opts.methodTimeout === undefined ? 15000 : opts.methodTimeout;
        await page.waitForSelector(METHOD_ITEM_SELECTOR, { state: 'attached', timeout: methodTimeout });
    }
    return { groups, clicks: expanded.clicks, stillCollapsed: expanded.stillCollapsed };
}

// Single static export object — see the MODULE FORMAT note above.
module.exports = {
    SERVICE_GROUP_SELECTOR,
    SERVICE_HEADER_SELECTOR,
    METHOD_ITEM_SELECTOR,
    COLLAPSED_SERVICE_HEADER_SELECTOR,
    waitForCatalogue,
    expandAllServices,
    openCatalogue,
};
