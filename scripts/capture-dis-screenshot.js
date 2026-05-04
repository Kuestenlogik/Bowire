/**
 * Captures a protocol-specific DIS screenshot for the home-page
 * protocol-card popup. Drives the locally-running DIS sample via
 * Playwright, picks the Exercise/monitor server-stream, waits for
 * convoy PDUs, then shoots dark + light.
 *
 * Assumes Kuestenlogik.Bowire.Protocol.Dis.Sample is running on
 * http://localhost:5082 with dis://239.1.2.3:3000 pre-seeded.
 */
const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const OUT = path.resolve(__dirname, '..', 'site', 'assets', 'images', 'screenshots');
const DOCS_OUT = path.resolve(__dirname, '..', 'docs', 'images', 'screenshots');
for (const dir of [OUT, DOCS_OUT]) if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });

const URL = 'http://localhost:5082/bowire';
const THEME = (process.env.THEME || 'dark').toLowerCase();
const NAME = 'streaming-dis';

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
    await page.evaluate((theme) => { try { localStorage.setItem('bowire-theme', theme); } catch (_) {} }, THEME);
    await page.reload({ waitUntil: 'networkidle' });

    await page.waitForSelector('.bowire-method-item', { timeout: 30000 });
    log('sidebar populated');

    // The Exercise service is the broad PDU stream — picks every entity.
    await page.locator('.bowire-method-item', { hasText: 'monitor' }).first().click();
    await page.waitForTimeout(400);

    await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
    log('stream started — waiting 5s for convoy PDUs');
    await page.waitForTimeout(5000);

    const file = path.join(OUT, `${NAME}-${THEME}.png`);
    await page.screenshot({ path: file, fullPage: false });
    fs.copyFileSync(file, path.join(DOCS_OUT, `${NAME}-${THEME}.png`));
    log(`  -> ${NAME}-${THEME}.png  (site + docs)`);

    const cancelBtn = page.locator('.bowire-cancel-btn, #bowire-cancel-btn').first();
    if (await cancelBtn.isVisible().catch(() => false)) await cancelBtn.click().catch(() => {});

    await browser.close();
})().catch(err => { console.error(err); process.exit(1); });
