/**
 * Captures the AsyncAPI sidebar screenshot for the home-page protocol
 * tile + the AsyncAPI roadmap section. Drives a standalone Bowire pointed
 * at the AsyncApi sample (schema server) + the Mqtt sample (broker) and
 * snaps the sidebar showing the two AsyncAPI channels with their
 * direction-arrow method badges.
 *
 * Preconditions (started by the caller before invoking this script):
 *   1. Kuestenlogik.Bowire.Samples.Mqtt      — broker on mqtt://localhost:1883
 *   2. Kuestenlogik.Bowire.Samples.AsyncApi  — schema on https://localhost:5120
 *   3. bowire --url https://localhost:5120/asyncapi.yaml \
 *             --url mqtt://localhost:1883 \
 *             --allow-self-signed-certs
 *      (workbench on http://localhost:5080)
 *
 * Set THEME=light to capture the light variant. Writes
 * site/assets/images/screenshots/asyncapi-discovery-${theme}.png (with a
 * docs/ mirror).
 */
const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');
// Canonical sidebar helpers — see scripts/lib/sidebar.cjs (#551).
const sidebar = require('../lib/sidebar.cjs');

const OUT = path.resolve(__dirname, '..', '..', 'site', 'assets', 'images', 'screenshots');
const DOCS_OUT = path.resolve(__dirname, '..', '..', 'docs', 'images', 'screenshots');
for (const dir of [OUT, DOCS_OUT]) if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });

const URL = 'http://localhost:5080';
const THEME = (process.env.THEME || 'dark').toLowerCase();
const NAME = 'asyncapi-discovery';

function log(m) { console.log(new Date().toISOString().slice(11, 19), m); }

(async () => {
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({
        viewport: { width: 1280, height: 720 },
        deviceScaleFactor: 2,
        ignoreHTTPSErrors: true,
        colorScheme: THEME === 'light' ? 'light' : 'dark',
    });
    const page = await ctx.newPage();

    log(`navigating to ${URL} (theme=${THEME})`);
    await page.goto(URL, { waitUntil: 'networkidle' });

    // Force the theme via localStorage so the UI matches the requested
    // mode regardless of the OS preference Playwright reported.
    await page.evaluate((theme) => {
        try { localStorage.setItem('bowire-theme', theme); } catch (_) {}
    }, THEME);
    await page.reload({ waitUntil: 'networkidle' });

    // The AsyncAPI sample declares two channels (craneStatus,
    // publisherStatus); each becomes a service node with one
    // receive-direction method. Wait for the SECOND service group so we
    // know discovery finished, not just the first frame — and do it
    // before expanding, because a group discovered after the expand
    // sweep would stay shut (the api.js <= 5-service auto-expand only
    // fires while nothing is persisted, which our clicks undo).
    await page.waitForFunction(
        (sel) => document.querySelectorAll(sel).length >= 2,
        sidebar.SERVICE_GROUP_SELECTOR,
        { timeout: 30000 });
    await sidebar.openCatalogue(page, { timeout: 30000 });
    await page.waitForFunction(
        (sel) => document.querySelectorAll(sel).length >= 2,
        sidebar.METHOD_ITEM_SELECTOR,
        { timeout: 30000 });
    log('sidebar populated — at least two methods visible');

    // Click the first receive operation to surface the AsyncAPI method
    // detail in the main pane (gives the screenshot context: badge +
    // operation summary + send/receive direction). Use the operation
    // name from the sample doc so the screenshot stays meaningful even
    // if other discoveries seep in.
    await page.locator('.bowire-method-item', { hasText: 'receiveCraneStatus' })
        .first().click();
    await page.waitForTimeout(400);

    const file = path.join(OUT, `${NAME}-${THEME}.png`);
    await page.screenshot({ path: file, fullPage: false });
    fs.copyFileSync(file, path.join(DOCS_OUT, `${NAME}-${THEME}.png`));
    log(`  -> ${NAME}-${THEME}.png  (site + docs)`);

    await browser.close();
})().catch(err => { console.error(err); process.exit(1); });
