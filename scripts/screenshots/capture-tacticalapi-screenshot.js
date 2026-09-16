/**
 * Captures the TacticalAPI map screenshots:
 *
 *   tacticalapi-map-{dark,light}.png   workbench, JSON left / map right —
 *                                      the pair the protocols.html popup
 *                                      cross-fades to (site + docs)
 *   map-widget-pins.png                the map pane maximised, dark —
 *                                      docs/features/map-widget.md
 *
 * Orchestrates three things: the TacticalAPI plugin is published into a
 * staging plugin-dir, the plugin repo's sample server (thirteen
 * MIL-2525C tracks under seven 2525D control measures, four blue
 * forces, own pose) is spawned, and the
 * locally built Tool is started on :5079 with the plugin loaded and
 * pointed at the sample's h2c port. Playwright then creates a
 * workspace, opens Discover, subscribes to
 * Situation.SubscribeSituationObjectEvents, waits until every SIDC the
 * map has seen is drawn by milsymbol and every graphic by mil-sym-ts,
 * and shoots.
 *
 * The basemap is forced to ESRI World Imagery (the `satellite` alias)
 * via a setter trap on `window.__BOWIRE_CONFIG__`, so the shot shows
 * aerial tiles instead of the offline default.
 *
 * Prerequisites (sibling checkout layout):
 *   ../Bowire.Protocol.TacticalApi        the plugin repo, with its
 *                                         sample under samples/
 *   artifacts/bin/Kuestenlogik.Bowire.Tool/release/bowire.dll
 *                                         `dotnet build src/Kuestenlogik.Bowire.Tool -c Release`
 *                                         — stop any running Tool first,
 *                                         a running one keeps the old DLL
 *
 * Run:
 *   node scripts/screenshots/capture-tacticalapi-screenshot.js
 *   THEME=dark node scripts/screenshots/capture-tacticalapi-screenshot.js
 *   node scripts/screenshots/capture-tacticalapi-screenshot.js --no-spawn
 *       (workbench on :5079 and sample on :5192 already running)
 *
 * Afterwards: `node scripts/site/optimize-images.mjs` regenerates the
 * AVIF/WebP variants of the site copies. It is mtime-driven, so only
 * the PNGs this script wrote are re-encoded.
 */
const { chromium } = require('@playwright/test');
const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');
const http = require('http');
const sidebar = require('../lib/sidebar.cjs');

const REPO = path.resolve(__dirname, '..', '..');
const PLUGIN_REPO = path.resolve(REPO, '..', 'Bowire.Protocol.TacticalApi');
const PLUGIN_PROJECT = path.join(PLUGIN_REPO, 'src', 'Kuestenlogik.Bowire.Protocol.TacticalApi');
const SAMPLE_PROJECT = path.join(PLUGIN_REPO, 'samples', 'Kuestenlogik.Bowire.Protocol.TacticalApi.Sample');
const PLUGIN_STAGING = path.join(REPO, 'artifacts', 'capture-plugin-staging');
const BOWIRE_CLI = path.join(REPO, 'artifacts', 'bin', 'Kuestenlogik.Bowire.Tool', 'release', 'bowire.dll');

// The sample listens on two cleartext ports: :5191 HTTP/1.1 (its own
// embedded workbench + gRPC-Web), :5192 h2c for native gRPC. The Tool
// dials the native port.
const SAMPLE_GRPC_URL = 'http://localhost:5192';
const SAMPLE_HEALTH_URL = 'http://localhost:5191/bowire';
const BOWIRE_UI_URL = 'http://localhost:5079/';

const OUT = path.join(REPO, 'site', 'assets', 'images', 'screenshots');
const DOCS_OUT = path.join(REPO, 'docs', 'images', 'screenshots');
for (const dir of [OUT, DOCS_OUT]) fs.mkdirSync(dir, { recursive: true });

const themesArg = (process.env.THEME || '').toLowerCase();
const THEMES = themesArg ? [themesArg] : ['dark', 'light'];
const SPAWN = !process.argv.includes('--no-spawn');

function log(m) { console.log(new Date().toISOString().slice(11, 19), m); }

function waitForHealth(url, timeoutMs) {
    return new Promise((resolve, reject) => {
        const deadline = Date.now() + timeoutMs;
        const tick = () => {
            const req = http.get(url, { timeout: 2000 }, (res) => {
                res.resume();
                if (res.statusCode && res.statusCode < 500) return resolve();
                if (Date.now() > deadline) return reject(new Error('timeout: ' + url));
                setTimeout(tick, 750);
            });
            req.on('error', () => {
                if (Date.now() > deadline) return reject(new Error('timeout: ' + url));
                setTimeout(tick, 750);
            });
            req.on('timeout', () => req.destroy());
        };
        tick();
    });
}

function run(cmd, args, opts) {
    return new Promise((resolve, reject) => {
        const proc = spawn(cmd, args, {
            stdio: ['ignore', 'inherit', 'inherit'],
            shell: process.platform === 'win32',
            ...opts,
        });
        proc.on('exit', (code) => code === 0 ? resolve() : reject(new Error(`${cmd} ${args[0]} exited ${code}`)));
    });
}

/**
 * `--plugin-dir` scans for one sub-directory per plugin; `dotnet publish
 * -o` writes exactly that shape. A populated staging dir is reused.
 */
async function publishPlugin() {
    const subDir = path.join(PLUGIN_STAGING, 'Kuestenlogik.Bowire.Protocol.TacticalApi');
    if (fs.existsSync(path.join(subDir, 'Kuestenlogik.Bowire.Protocol.TacticalApi.dll'))) {
        log(`Plugin already staged at ${subDir}`);
        return;
    }
    log('Publishing TacticalAPI plugin into staging dir…');
    fs.mkdirSync(subDir, { recursive: true });
    await run('dotnet', ['publish', PLUGIN_PROJECT, '-c', 'Release', '-o', subDir]);
}

function spawnDetached(name, cmd, args, env) {
    const proc = spawn(cmd, args, {
        stdio: ['ignore', 'inherit', 'inherit'],
        shell: process.platform === 'win32',
        env: { ...process.env, ...(env || {}) },
    });
    proc.on('exit', (code, sig) => log(`${name} exited (code=${code} signal=${sig})`));
    return proc;
}

/**
 * Get from the page the Tool serves to a populated Discover sidebar.
 *
 * Since #646 the workspace list belongs to the signed-in identity and
 * lives on the server; a fresh browser profile therefore boots into the
 * Start page with the "create a workspace" CTA, and the locked server
 * URL only comes into play once a workspace exists. Creating one through
 * the dialog is the same path an operator takes. Methods then live in
 * the Discover rail, not on the Start page.
 */
async function openDiscover(page) {
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
    const welcome = page.locator('#bowire-welcome-create-btn');
    if (await welcome.isVisible().catch(() => false)) {
        await welcome.click();
        await page.waitForSelector('.bowire-ws-create-dialog .bowire-prompt-input', { timeout: 5000 });
        await page.locator('.bowire-ws-create-dialog .bowire-prompt-input').first().fill('TacticalAPI');
        await page.locator('.bowire-ws-create-dialog [data-tpl-id="empty"]').click();
        await page.locator('.bowire-ws-create-dialog .bowire-confirm-btn:not(.cancel)').click();
        log('  workspace created');
    }
    const rail = page.locator('.bowire-rail-btn[data-rail-mode-id="discover"]');
    if (await rail.isVisible().catch(() => false)) {
        await rail.click();
    } else {
        // Height-constrained rail: the mode sits in the overflow popover.
        await page.locator('#bowire-rail-overflow-btn').click();
        await page.locator('.bowire-rail-overflow-popover-item[data-rail-mode-id="discover"]').click();
    }
    await sidebar.openCatalogue(page, { timeout: 45000 });
    log('  sidebar populated');
}

/**
 * The widget on screen. `window.__bowireMapWidgets` can also hold an
 * earlier mount whose container morphdom has already replaced; that one
 * is detached, still registered, and answers every call — so every
 * page-side lookup below filters by `isConnected` first. (Repeated
 * inline because a page function cannot close over Node-side code.)
 */
async function capture(theme, { maximize, shotName, docsOnly }) {
    log(`---- ${shotName}-${theme}${maximize ? ' (maximised)' : ''} ----`);
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({
        viewport: { width: 1440, height: 900 },
        deviceScaleFactor: 2,
        locale: 'en-US',
        colorScheme: theme,
    });
    // The inline <script> the server emits assigns __BOWIRE_CONFIG__ once;
    // intercept the assignment and fold in the satellite basemap.
    await ctx.addInitScript(() => {
        let _config = null;
        Object.defineProperty(window, '__BOWIRE_CONFIG__', {
            configurable: true,
            get() { return _config; },
            set(v) { _config = Object.assign({}, v || {}, { mapBasemap: 'satellite' }); }
        });
    });
    const page = await ctx.newPage();
    page.on('console', (msg) => {
        if (msg.type() === 'error') log(`  [page error] ${msg.text()}`);
    });
    page.on('pageerror', (err) => log(`  [page error] ${err.message}`));

    await page.goto(BOWIRE_UI_URL, { waitUntil: 'domcontentloaded' });
    await page.evaluate((t) => { try { localStorage.setItem('bowire_theme_pref', t); } catch (_) {} }, theme);
    await page.reload({ waitUntil: 'domcontentloaded' });

    await openDiscover(page);
    await page.locator('.bowire-method-item', { hasText: 'SubscribeSituationObjectEvents' }).first().click();
    await page.waitForTimeout(500);
    await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
    log('  stream started');

    await page.waitForFunction(() => document.querySelector('.maplibregl-canvas') != null, null, { timeout: 20000 });
    // Every SIDC the map has seen is either drawn or given up on — no
    // pin is still showing the affinity fallback for want of time.
    await page.waitForFunction(() => {
        const h = (window.__bowireMapWidgets || []).filter((w) => w.container.isConnected)[0];
        if (!h || typeof h.symbolIcons !== 'function') return false;
        const states = Object.values(h.symbolIcons());
        return states.length > 0 && states.every((s) => s === 'ok' || s === 'failed');
    }, null, { timeout: 20000 });
    log('  symbols: ' + JSON.stringify(await page.evaluate(() =>
        (window.__bowireMapWidgets || []).filter((w) => w.container.isConnected)[0].symbolIcons())));
    // The overlay's graphics likewise: mil-sym-ts has landed and every
    // one of them is drawn by it, not standing in as its bare geometry.
    await page.waitForFunction(() => {
        const h = (window.__bowireMapWidgets || []).filter((w) => w.container.isConnected)[0];
        if (!h || typeof h.graphics !== 'function') return false;
        const g = h.graphics();
        return g.library === 'loaded' && g.items.length > 0 && g.items.every((i) => i.drawn);
    }, null, { timeout: 30000 });
    log('  graphics: ' + JSON.stringify(await page.evaluate(() =>
        (window.__bowireMapWidgets || []).filter((w) => w.container.isConnected)[0].graphics().items
            .map((i) => i.kind + ':' + i.designation))));

    // Room for the map: fold the tracks legend, hide the message list.
    await page.evaluate(() => {
        const h = (window.__bowireMapWidgets || []).filter((w) => w.container.isConnected)[0];
        const head = h && h.container.querySelector('.bowire-map-track-ctrl > div');
        if (head && head.textContent.indexOf('▾') >= 0) head.click();
    });
    const hideList = page.locator('[id$="bowire-stream-maximize-btn"]').first();
    if (await hideList.isVisible().catch(() => false)) await hideList.click();
    if (maximize) {
        await page.locator('.bowire-widget-pane-maximize').first().click();
        await page.waitForTimeout(800);
    }
    // Satellite tiles for the fitted viewport.
    await page.waitForTimeout(4500);

    const file = path.join(docsOnly ? DOCS_OUT : OUT, `${shotName}-${theme}.png`);
    await page.screenshot({ path: file, fullPage: false });
    if (!docsOnly) fs.copyFileSync(file, path.join(DOCS_OUT, `${shotName}-${theme}.png`));
    log(`  -> ${file}`);

    const cancelBtn = page.locator('.bowire-cancel-btn, #bowire-cancel-btn').first();
    if (await cancelBtn.isVisible().catch(() => false)) await cancelBtn.click().catch(() => {});
    await browser.close();
    return file;
}

(async () => {
    let sampleProc = null;
    let bowireProc = null;
    if (SPAWN) {
        if (!fs.existsSync(BOWIRE_CLI)) throw new Error(`Tool not built: ${BOWIRE_CLI}`);
        if (!fs.existsSync(SAMPLE_PROJECT)) throw new Error(`Sample not found: ${SAMPLE_PROJECT}`);
        await publishPlugin();

        log('Starting TacticalAPI sample (:5191 web, :5192 h2c)…');
        sampleProc = spawnDetached('sample', 'dotnet', ['run', '--project', SAMPLE_PROJECT, '-c', 'Release']);
        await waitForHealth(SAMPLE_HEALTH_URL, 120000);
        log('Sample ready.');

        log('Starting Bowire Tool (:5079) with the staged plugin…');
        bowireProc = spawnDetached('bowire', 'dotnet', [
            BOWIRE_CLI, '--port', '5079', '--no-browser',
            '--plugin-dir', PLUGIN_STAGING,
            '--url', `tacticalapi@${SAMPLE_GRPC_URL}`,
        ]);
        await waitForHealth(BOWIRE_UI_URL, 60000);
        log('Workbench ready.');
    } else {
        log('--no-spawn: expecting the workbench on :5079 and the sample on :5192');
        await waitForHealth(BOWIRE_UI_URL, 5000);
    }

    try {
        for (const theme of THEMES) {
            await capture(theme, { maximize: false, shotName: 'tacticalapi-map', docsOnly: false });
        }
        if (THEMES.includes('dark')) {
            // The unsuffixed file is the dark variant — the convention every
            // screenshot pair under site/assets/images/screenshots follows.
            for (const dir of [OUT, DOCS_OUT]) {
                fs.copyFileSync(path.join(dir, 'tacticalapi-map-dark.png'), path.join(dir, 'tacticalapi-map.png'));
            }
            // The widget doc's hero: the map alone, downscaled — a 2x
            // satellite frame is ~5 MB as PNG, 1600 px wide is a third.
            const shot = await capture('dark', { maximize: true, shotName: 'map-widget-pins', docsOnly: true });
            const sharp = require('sharp');
            const hero = path.join(DOCS_OUT, 'map-widget-pins.png');
            const buf = await sharp(shot).resize({ width: 1600 }).png({ compressionLevel: 9 }).toBuffer();
            fs.writeFileSync(hero, buf);
            fs.unlinkSync(shot);
            log(`  -> ${hero}`);
        }
    } finally {
        for (const [name, proc] of [['bowire', bowireProc], ['sample', sampleProc]]) {
            if (!proc) continue;
            log(`Shutting down ${name}…`);
            if (process.platform === 'win32') {
                // `shell: true` put a cmd.exe in front of dotnet; kill the tree.
                spawn('taskkill', ['/pid', String(proc.pid), '/T', '/F'], { stdio: 'ignore', shell: true });
            } else {
                try { proc.kill('SIGTERM'); } catch (_) {}
            }
        }
    }
})().catch((err) => { console.error(err); process.exit(1); });
