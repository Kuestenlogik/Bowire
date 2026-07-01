/**
 * Standalone first-run capture (C1 / Phase B).
 *
 * Captures the genuine first-boot welcome by targeting the standalone
 * Tool (bowire.exe --no-browser --port 5180) and wiping localStorage
 * before reload — yielding workspaces=[] + activeWorkspaceId=null,
 * which renders the "Create your first workspace" Home CTA.
 *
 * The main capture-screenshots.js targets the Combined sample, whose
 * in-process EndpointDataSource auto-populates services[] — so its
 * detectLandingState() never returns 'first-run'. This file is a thin
 * runner that exercises only the first-run scene against the Tool.
 *
 * Theme is set via THEME=dark|light env var (default dark). The legacy
 * theme-less PNG is written when THEME=dark, matching the main script's
 * shot()/shotQuickstart() mirror behaviour.
 */
const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const OUT = path.resolve(__dirname, '..', '..', 'site', 'assets', 'images', 'screenshots');
const QUICKSTART_OUT = path.resolve(__dirname, '..', '..', 'docs', 'images');
const TOOL_URL = process.env.TOOL_URL || 'http://localhost:5180/';
const THEME = (process.env.THEME || 'dark').toLowerCase();

for (const dir of [OUT, QUICKSTART_OUT]) {
    if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
}

function log(m) { console.log(new Date().toISOString().slice(11, 19), m); }

(async () => {
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({
        viewport: { width: 1280, height: 720 },
        ignoreHTTPSErrors: true,
        colorScheme: THEME
    });
    const page = await ctx.newPage();
    await page.emulateMedia({ colorScheme: THEME });

    log(`first-run (standalone Tool, theme=${THEME}) → ${TOOL_URL}`);
    await page.goto(TOOL_URL, { waitUntil: 'domcontentloaded' });
    // Wipe every storage layer that might carry a prior boot's
    // workspace seed / rail mode / URL list — anything left would
    // bypass the no-workspace branch and render Continue/Recent
    // bands instead of the welcome card.
    await page.evaluate(async (theme) => {
        try { localStorage.clear(); } catch {}
        try { sessionStorage.clear(); } catch {}
        try {
            if (window.indexedDB && indexedDB.databases) {
                const dbs = await indexedDB.databases();
                await Promise.all(dbs.map(d => new Promise(res => {
                    const req = indexedDB.deleteDatabase(d.name);
                    req.onsuccess = req.onerror = req.onblocked = () => res();
                })));
            }
        } catch {}
        // Theme is the only pref we DO seed — without it the page
        // flashes light briefly before settling.
        try { localStorage.setItem('bowire_theme_pref', theme); } catch {}
    }, THEME);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
    await page.waitForSelector('.bowire-home-band-firstrun', { timeout: 10000 });
    await page.waitForTimeout(800);

    const ok = await page.evaluate(() => {
        const band = document.querySelector('.bowire-home-band-firstrun');
        if (!band) return false;
        const txt = (band.innerText || '').toLowerCase();
        return txt.includes('workspace');
    });
    if (!ok) {
        log('  (welcome did NOT render — skipping write)');
        process.exitCode = 1;
    } else {
        const docsFile = path.join(QUICKSTART_OUT, `bowire-first-run-${THEME}.png`);
        const siteFile = path.join(OUT, `first-run-${THEME}.png`);
        await page.screenshot({ path: docsFile, fullPage: false });
        fs.copyFileSync(docsFile, siteFile);
        if (THEME === 'dark') {
            fs.copyFileSync(docsFile, path.join(QUICKSTART_OUT, 'bowire-first-run.png'));
            fs.copyFileSync(docsFile, path.join(OUT, 'first-run.png'));
        }
        log(`  -> bowire-first-run-${THEME}.png  (docs + screenshots mirror)`);
    }

    await ctx.close();
    await browser.close();
})();
