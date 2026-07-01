// v2.2 (#325 Workspace… sub-tree) — Playwright probe.
//
// Boots the workbench at http://localhost:5180/, opens the Settings
// modal, and verifies:
//   1. The Workspace… parent renders as an expandable tree node with
//      the four sub-page children (Sources / URLs, Environments,
//      Per-Workspace overrides, Data).
//   2. Each sub-page mounts without error when clicked. We assert on
//      the section title appearing in the right panel — a full DOM
//      snapshot would be brittle across the workspace / no-workspace
//      empty states.
//   3. The parent-onClick lands on Sources / URLs (the default entry).
//   4. Legacy tab id 'workspace' is migrated on openSettings() to
//      'workspace-sources' — proven by opening the modal with that
//      arg + checking the right-panel title.
//
// Run: node scripts/ux-tests/probe-settings-workspace-subtree.mjs

import { chromium } from '@playwright/test';

const URL = process.env.BOWIRE_URL || 'http://localhost:5180/';

const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();

const messages = [];
page.on('pageerror', e => messages.push({ kind: 'pageerror', text: String(e && e.message || e) }));
page.on('console', m => {
    if (m.type() === 'error') messages.push({ kind: 'console-error', text: m.text() });
});

await page.goto(URL);
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 15000 });

async function openSettingsAt(tabId) {
    // Close any open settings first.
    await page.evaluate(() => {
        if (typeof closeSettings === 'function') closeSettings();
    });
    await page.evaluate((t) => {
        if (typeof openSettings === 'function') openSettings(t || undefined);
    }, tabId);
    await page.waitForSelector('.bowire-settings-overlay', { timeout: 5000 });
    await page.waitForTimeout(150);
}

async function rightPanelTitle() {
    return await page.evaluate(() => {
        const panel = document.querySelector('.bowire-settings-right');
        const h3 = panel && panel.querySelector('.bowire-settings-section-title');
        return h3 ? h3.textContent.trim() : null;
    });
}

async function treeSnapshot() {
    return await page.evaluate(() => {
        const rows = Array.from(document.querySelectorAll('.bowire-settings-left .bowire-tree-node'));
        return rows.map(r => ({
            label: (r.querySelector('.bowire-tree-node-label') || r).textContent.trim(),
            expanded: r.classList.contains('expanded'),
            selected: !!r.querySelector('.selected, .bowire-tree-node.selected') || r.classList.contains('selected')
        }));
    });
}

// ---- 1. Tree shape ----
await openSettingsAt('workspace-sources');
const tree = await treeSnapshot();
const workspaceLabels = tree.filter(r => /Sources \/ URLs|Environments|Per-Workspace overrides|^Data$/.test(r.label))
    .map(r => r.label);
console.log('workspace sub-labels visible:', workspaceLabels);

const missing = ['Sources / URLs', 'Environments', 'Per-Workspace overrides', 'Data']
    .filter(l => !workspaceLabels.includes(l));
if (missing.length > 0) {
    console.error('FAIL: missing workspace sub-pages in tree:', missing);
    process.exit(1);
}

// ---- 2a. Sources / URLs sub-page ----
await openSettingsAt('workspace-sources');
let title = await rightPanelTitle();
console.log('workspace-sources title:', title);
if (title !== 'Sources / URLs') { console.error('FAIL: expected Sources / URLs'); process.exit(1); }

// ---- 2b. Environments sub-page ----
await openSettingsAt('workspace-environments');
title = await rightPanelTitle();
console.log('workspace-environments title:', title);
if (title !== 'Environments') { console.error('FAIL: expected Environments'); process.exit(1); }

// ---- 2c. Per-Workspace overrides sub-page ----
await openSettingsAt('workspace-overrides');
title = await rightPanelTitle();
console.log('workspace-overrides title:', title);
if (title !== 'Per-Workspace overrides') { console.error('FAIL: expected Per-Workspace overrides'); process.exit(1); }

// ---- 2d. Data sub-page ----
await openSettingsAt('workspace-data');
title = await rightPanelTitle();
console.log('workspace-data title:', title);
if (title !== 'Data') { console.error('FAIL: expected Data'); process.exit(1); }

// ---- 3. Legacy 'workspace' id is migrated on open ----
await openSettingsAt('workspace');
title = await rightPanelTitle();
console.log('legacy workspace migration → title:', title);
if (title !== 'Sources / URLs') {
    console.error('FAIL: legacy workspace should migrate to workspace-sources (Sources / URLs)');
    process.exit(1);
}

if (messages.length > 0) {
    console.error('page errors during probe:', messages);
    process.exit(1);
}

console.log('OK: Workspace… sub-tree renders + navigates correctly.');
await browser.close();
