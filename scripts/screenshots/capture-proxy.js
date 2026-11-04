/**
 * Captures the Intercept rail with flows recorded by the workbench's reverse
 * proxy, for the site's "Intercepting proxy" sections (#977). Writes
 * proxy-dark.png and proxy-light.png into site/assets/images/screenshots/;
 * run `npm run optimize-images` afterwards for the AVIF/WebP variants.
 *
 * Drives a running standalone Tool. Start one, start a reverse proxy in front
 * of a public echo service and send it some traffic first:
 *
 *     bowire --port 5180 --no-browser
 *     curl -X POST -H "Content-Type: application/json" \
 *          -d '{"upstream":"http://httpbin.org","port":5200}' \
 *          http://localhost:5180/api/tools/reverse-proxy/start
 *     curl "http://127.0.0.1:5200/get?pet=ada"
 *     curl -X POST -H "Content-Type: application/json" -d '{"name":"ada","age":3}' http://127.0.0.1:5200/post
 *     curl http://127.0.0.1:5200/status/404   # and a few more
 *
 * Then:
 *
 *     BOWIRE_URL=http://localhost:5180/ node scripts/screenshots/capture-proxy.js
 *
 * The page runs in en-US: the workbench follows the browser's language.
 */
const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const URL = process.env.BOWIRE_URL || process.argv[2] || 'http://localhost:5080/';
const OUT_DIR = path.resolve(__dirname, '..', '..', 'site', 'assets', 'images', 'screenshots');

function log(m) { console.log(new Date().toISOString().slice(11, 19), m); }

async function capture(browser, theme) {
    const context = await browser.newContext({
        viewport: { width: 1280, height: 720 },
        locale: 'en-US',
        colorScheme: theme,
    });
    try {
        const page = await context.newPage();
        await page.goto(URL, { waitUntil: 'domcontentloaded' });
        await page.evaluate((t) => {
            localStorage.setItem('bowire_theme_pref', t);
            localStorage.setItem('bowire_rail_mode', 'intercept');
        }, theme);
        await page.reload({ waitUntil: 'domcontentloaded' });
        await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });

        // The POST carries a request body, so the detail pane shows both sides.
        const rows = page.locator('#bowire-sidebar-list-intercept .bowire-proxy-list-item');
        await rows.first().waitFor({ timeout: 15000 });
        const post = rows.filter({ has: page.locator('.bowire-proxy-list-method', { hasText: 'POST' }) }).first();
        await ((await post.count()) ? post : rows.first()).click();
        await page.waitForLoadState('networkidle');

        const out = path.join(OUT_DIR, `proxy-${theme}.png`);
        await page.screenshot({ path: out, fullPage: false });
        log(`saved ${out}`);
    } finally {
        await context.close();
    }
}

(async () => {
    if (!fs.existsSync(OUT_DIR)) fs.mkdirSync(OUT_DIR, { recursive: true });
    const browser = await chromium.launch();
    try {
        for (const theme of ['dark', 'light']) await capture(browser, theme);
    } finally {
        await browser.close();
    }
})().catch((e) => { console.error(e); process.exit(1); });
