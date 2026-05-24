/**
 * Captures a protocol-specific AMQP screenshot for the home-page
 * protocol-card popup. Drives the locally-running Bowire.Samples.Amqp
 * via Playwright, picks the harbor / crane.*.status server-stream,
 * waits for a few crane-telemetry frames, then shoots dark + light.
 *
 * Assumes Kuestenlogik.Bowire.Samples.Amqp is running on
 * https://localhost:5118 with amqp://localhost:5672 pre-seeded and
 * the docker-compose RabbitMQ broker up.
 *
 * Run: THEME=dark node scripts/capture-amqp-screenshot.js
 *      THEME=light node scripts/capture-amqp-screenshot.js
 */
const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const OUT = path.resolve(__dirname, '..', 'site', 'assets', 'images', 'screenshots');
const DOCS_OUT = path.resolve(__dirname, '..', 'docs', 'images', 'screenshots');
for (const dir of [OUT, DOCS_OUT]) if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });

const URL = 'https://localhost:5118/bowire';
const THEME = (process.env.THEME || 'dark').toLowerCase();
const NAME = 'streaming-amqp';

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

    // RabbitMQ exposes the 'harbor' exchange with one routing-key node
    // per published crane status. The plugin's discovery surfaces a
    // 'receive' method for each. Click the first 'receive' (consume)
    // method we find — Bowire's sidebar lists them top-down in the
    // order discovery returns them, and there's no other consume
    // action competing for the slot.
    const receives = page.locator('.bowire-method-item', { hasText: 'receive' });
    const receiveCount = await receives.count();
    log(`found ${receiveCount} 'receive' method(s) — picking the first`);
    await receives.first().click();
    await page.waitForTimeout(400);

    await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
    log('stream started — waiting 8s for crane events (~1 s cadence, 3 cranes)');
    await page.waitForTimeout(8000);

    const file = path.join(OUT, `${NAME}-${THEME}.png`);
    await page.screenshot({ path: file, fullPage: false });
    fs.copyFileSync(file, path.join(DOCS_OUT, `${NAME}-${THEME}.png`));
    log(`  -> ${NAME}-${THEME}.png  (site + docs)`);

    const cancelBtn = page.locator('.bowire-cancel-btn, #bowire-cancel-btn').first();
    if (await cancelBtn.isVisible().catch(() => false)) await cancelBtn.click().catch(() => {});

    await browser.close();
})().catch(err => { console.error(err); process.exit(1); });
