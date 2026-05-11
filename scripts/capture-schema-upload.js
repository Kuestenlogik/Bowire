/**
 * Captures the schema-upload state by:
 *   1. expecting a bowire process running in first-run mode (no --url)
 *      on the configured port (default 5185 — start it externally before
 *      invoking this script);
 *   2. POSTing a real .proto into /api/proto/upload — the same endpoint
 *      the in-UI drop zone uses, so the resulting visual is byte-for-
 *      byte what a user gets after dropping a file;
 *   3. driving Playwright to navigate, switch the source-mode tab to
 *      "Proto", wait until the discovered services render, and shoot
 *      the page in both themes.
 *
 * We deliberately don't go through the Playwright `setInputFiles` path —
 * the in-UI drop zone constructs its <input type="file"> dynamically
 * inside the click handler and immediately calls .click(), so there's
 * no stable element to target. POSTing to the same endpoint the click
 * handler hits is the cleaner reproduction.
 *
 * Outputs:
 *   site/assets/images/screenshots/schema-upload-{dark,light}.png
 *   site/assets/images/screenshots/schema-upload.png    (THEME=dark only)
 *   docs/images/bowire-schema-upload-{dark,light}.png
 *   docs/images/bowire-schema-upload.png                (THEME=dark only)
 */
const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const SITE_OUT = path.resolve(__dirname, '..', 'site', 'assets', 'images', 'screenshots');
const DOCS_OUT = path.resolve(__dirname, '..', 'docs', 'images');
const PORT = Number(process.env.PORT || 5185);
const THEME = (process.env.THEME || 'dark').toLowerCase();
const PROTO = process.env.PROTO || path.resolve(
    __dirname, '..', '..', 'Bowire.Samples',
    'src', 'Kuestenlogik.Bowire.Samples.Grpc', 'Protos', 'harbor.proto'
);

function log(m) { console.log(new Date().toISOString().slice(11, 19), m); }

async function uploadProto() {
    if (!fs.existsSync(PROTO)) {
        throw new Error(`proto file not found: ${PROTO}`);
    }
    const body = fs.readFileSync(PROTO, 'utf8');
    // Clear any previously uploaded protos so the screenshot is
    // deterministic — older captures could otherwise stack services.
    await fetch(`http://localhost:${PORT}/api/proto/upload`, { method: 'DELETE' }).catch(() => {});
    const res = await fetch(`http://localhost:${PORT}/api/proto/upload`, {
        method: 'POST',
        body,
        headers: { 'content-type': 'text/plain' },
    });
    if (!res.ok) {
        throw new Error(`/api/proto/upload returned ${res.status}: ${await res.text()}`);
    }
    log(`uploaded ${path.basename(PROTO)} (${body.length} bytes)`);
}

(async () => {
    log(`port ${PORT}, theme ${THEME}`);
    await uploadProto();

    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({
        viewport: { width: 1280, height: 720 },
        ignoreHTTPSErrors: true,
        colorScheme: THEME,
    });
    const page = await ctx.newPage();
    await page.emulateMedia({ colorScheme: THEME });

    await page.goto(`http://localhost:${PORT}/`, { waitUntil: 'domcontentloaded' });
    await page.evaluate((t) => {
        try { localStorage.setItem('bowire_theme_pref', t); } catch { }
        try { localStorage.setItem('bowire_source_mode', 'proto'); } catch { }
    }, THEME);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });

    // Make sure we're on the "Proto" source-mode tab — even with the
    // localStorage seed above the URL tab might still be selected on
    // first paint; clicking is idempotent if it's already active.
    const protoTab = page.locator('#bowire-source-tab-proto');
    if (await protoTab.count() > 0) {
        await protoTab.click().catch(() => {});
    }

    // Wait for either the imported services to render in the sidebar
    // or the drop-zone-with-"N services loaded" badge — both prove the
    // upload landed and the UI rebuilt itself off the new schema.
    await page.waitForFunction(() => {
        const t = (document.body && document.body.innerText) || '';
        return /service[s]? loaded|HarborService/i.test(t);
    }, { timeout: 20000 }).catch(() => {});
    await page.waitForTimeout(800);

    const siteFile = path.join(SITE_OUT, `schema-upload-${THEME}.png`);
    const docsFile = path.join(DOCS_OUT, `bowire-schema-upload-${THEME}.png`);
    await page.screenshot({ path: siteFile, fullPage: false });
    fs.copyFileSync(siteFile, docsFile);
    if (THEME === 'dark') {
        fs.copyFileSync(siteFile, path.join(SITE_OUT, 'schema-upload.png'));
        fs.copyFileSync(siteFile, path.join(DOCS_OUT, 'bowire-schema-upload.png'));
    }
    log(`  -> schema-upload-${THEME}.png`);

    await ctx.close();
    await browser.close();
    log('done');
})();
