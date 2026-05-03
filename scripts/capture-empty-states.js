/**
 * Captures the four "Smart empty states" by pointing Playwright at four
 * separate standalone-mode `bowire` instances — each launched with a
 * different URL setup so Bowire's UI lands in the matching state:
 *
 *   ready          → http://localhost:5181 (--url https://localhost:5101)
 *   first-run      → http://localhost:5182 (no --url)
 *   multi-url      → http://localhost:5183 (--url 5101 + --url 65535)
 *   discovery-failed → http://localhost:5184 (--url https://localhost:65535)
 *
 * Run separate `bowire --no-browser --port NNNN ...` processes before
 * invoking this script. Outputs land in BOTH:
 *   site/assets/images/screenshots/{name}-{theme}.png
 *   docs/images/bowire-{name}-{theme}.png
 * plus a theme-less default copy when THEME=dark.
 */
const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const SITE_OUT = path.resolve(__dirname, '..', 'site', 'assets', 'images', 'screenshots');
const DOCS_OUT = path.resolve(__dirname, '..', 'docs', 'images');
const THEME = (process.env.THEME || 'dark').toLowerCase();

const STATES = [
    { name: 'ready', port: 5181 },
    { name: 'first-run', port: 5182 },
    { name: 'multi-url', port: 5183 },
    { name: 'discovery-failed', port: 5184 },
];

function log(m) { console.log(new Date().toISOString().slice(11, 19), m); }

(async () => {
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({
        viewport: { width: 1280, height: 720 },
        ignoreHTTPSErrors: true,
        colorScheme: THEME,
    });
    const page = await ctx.newPage();
    await page.emulateMedia({ colorScheme: THEME });

    for (const { name, port } of STATES) {
        log(`${name} (port ${port}, theme=${THEME})…`);
        try {
            await page.goto(`http://localhost:${port}/bowire`, { waitUntil: 'domcontentloaded' });
            // Force the theme pref before first paint and reload so the
            // capture lands in the explicitly-themed render rather than
            // whatever the OS pref says.
            await page.evaluate((t) => {
                try { localStorage.setItem('bowire_theme_pref', t); } catch {}
            }, THEME);
            await page.reload({ waitUntil: 'domcontentloaded' });
            await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });

            // Wait until the discovery loop has either resolved (services
            // populated, or first-run landing card visible) or visibly
            // failed (error pane). 30 s is enough for a TCP RST on the
            // dead :65535 to surface.
            await page.waitForFunction(() => {
                const txt = (document.body && document.body.innerText) || '';
                if (/Discovering services/i.test(txt)) return false;
                return /Connected|services|First-run|Welcome|first request|Pick a method|Discovery failed|Server unreachable|Drop in a schema/i.test(txt);
            }, { timeout: 30000 }).catch(() => {});
            await page.waitForTimeout(800);

            const siteFile = path.join(SITE_OUT, `${name}-${THEME}.png`);
            const docsFile = path.join(DOCS_OUT, `bowire-${name}-${THEME}.png`);
            await page.screenshot({ path: siteFile, fullPage: false });
            fs.copyFileSync(siteFile, docsFile);
            if (THEME === 'dark') {
                fs.copyFileSync(siteFile, path.join(SITE_OUT, `${name}.png`));
                fs.copyFileSync(siteFile, path.join(DOCS_OUT, `bowire-${name}.png`));
            }
            log(`  -> ${name}-${THEME}.png`);
        } catch (e) {
            log(`  (${name} failed: ${e.message.split('\n')[0]})`);
        }
    }

    await ctx.close();
    await browser.close();
    log('done');
})();
