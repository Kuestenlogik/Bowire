/**
 * Captures a protocol-specific Akka.NET screenshot for the home-page
 * protocol-card popup. Drives the locally-running Akka sample via
 * Playwright, picks the Akka tab, executes Tap/MonitorMessages, waits
 * for a few mailbox-tap frames to arrive, then takes one shot.
 *
 * Assumes Kuestenlogik.Bowire.Protocol.Akka.Sample is running on
 * http://localhost:5080. Set THEME=light to capture the light variant.
 *
 * Writes 1280×720 PNGs to site/assets/images/screenshots/streaming-akka-${theme}.png
 * (with a docs/ mirror so DocFX shares the same artefact).
 */
const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const OUT = path.resolve(__dirname, '..', 'site', 'assets', 'images', 'screenshots');
const DOCS_OUT = path.resolve(__dirname, '..', 'docs', 'images', 'screenshots');
for (const dir of [OUT, DOCS_OUT]) if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });

const URL = 'http://localhost:5080/bowire';
const THEME = (process.env.THEME || 'dark').toLowerCase();
const NAME = 'streaming-akka';

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

    // Wait for the sidebar to populate. The Akka sample only exposes one
    // service ('Tap') with one method ('MonitorMessages').
    await page.waitForSelector('.bowire-method-item', { timeout: 30000 });
    log('sidebar populated');

    // Open MonitorMessages.
    await page.locator('.bowire-method-item', { hasText: 'MonitorMessages' }).first().click();
    await page.waitForTimeout(400);

    // Kick off the server stream.
    await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
    log('stream started — waiting 4s for harbour mailbox traffic');
    await page.waitForTimeout(4000);

    const file = path.join(OUT, `${NAME}-${THEME}.png`);
    await page.screenshot({ path: file, fullPage: false });
    fs.copyFileSync(file, path.join(DOCS_OUT, `${NAME}-${THEME}.png`));
    log(`  -> ${NAME}-${THEME}.png  (site + docs)`);

    // Cancel the stream before exit so the sample's console quiets down.
    const cancelBtn = page.locator('.bowire-cancel-btn, #bowire-cancel-btn').first();
    if (await cancelBtn.isVisible().catch(() => false)) await cancelBtn.click().catch(() => {});

    await browser.close();
})().catch(err => { console.error(err); process.exit(1); });
