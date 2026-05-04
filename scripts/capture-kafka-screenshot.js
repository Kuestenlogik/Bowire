/**
 * Captures a protocol-specific Kafka screenshot for the home-page
 * protocol-card popup. Drives the locally-running Kafka sample via
 * Playwright, picks the harbor.port-calls/consume server-stream,
 * waits for a few harbour port-call events, then shoots dark + light.
 *
 * Assumes Kuestenlogik.Bowire.Protocol.Kafka.Sample is running on
 * http://localhost:5083 with kafka://localhost:9092 pre-seeded and
 * the docker-compose Kafka cluster up.
 */
const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const OUT = path.resolve(__dirname, '..', 'site', 'assets', 'images', 'screenshots');
const DOCS_OUT = path.resolve(__dirname, '..', 'docs', 'images', 'screenshots');
for (const dir of [OUT, DOCS_OUT]) if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });

const URL = 'http://localhost:5083/bowire';
const THEME = (process.env.THEME || 'dark').toLowerCase();
const NAME = 'streaming-kafka';

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

    // Sidebar lists topics alphabetically: berth-status (5 s cadence),
    // port-calls (2 s cadence). Pick the SECOND 'consume' so we land on
    // port-calls — its faster cadence gives the screenshot enough frames
    // to render in 8 s.
    const consumes = page.locator('.bowire-method-item', { hasText: 'consume' });
    const consumeCount = await consumes.count();
    log(`found ${consumeCount} 'consume' methods — picking the second (port-calls)`);
    await consumes.nth(1).click();
    await page.waitForTimeout(400);

    await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
    log('stream started — waiting 8s for port-call events (~2 s cadence)');
    await page.waitForTimeout(8000);

    const file = path.join(OUT, `${NAME}-${THEME}.png`);
    await page.screenshot({ path: file, fullPage: false });
    fs.copyFileSync(file, path.join(DOCS_OUT, `${NAME}-${THEME}.png`));
    log(`  -> ${NAME}-${THEME}.png  (site + docs)`);

    const cancelBtn = page.locator('.bowire-cancel-btn, #bowire-cancel-btn').first();
    if (await cancelBtn.isVisible().catch(() => false)) await cancelBtn.click().catch(() => {});

    await browser.close();
})().catch(err => { console.error(err); process.exit(1); });
