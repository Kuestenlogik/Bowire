/**
 * Captures the TacticalAPI map screenshots:
 *
 *   tacticalapi-map-{dark,light}.png   workbench, JSON left / map right —
 *                                      the pair the protocols.html popup
 *                                      cross-fades to (site + docs)
 *   map-widget-pins.png                the map pane maximised, dark —
 *                                      docs/features/map-widget.md
 *   map-widget-graphics-hover.png      JSON viewer beside the map, the
 *                                      stream stopped, a vertex of the
 *                                      2525C-coded phase line hovered in
 *                                      the viewer and the graphic lit on
 *                                      the map — docs/features/map-widget.md
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
 *   ONLY_HOVER=1 THEME=dark node scripts/screenshots/capture-tacticalapi-screenshot.js
 *       (only the hover shot — the one that takes iterating)
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
async function capture(theme, { maximize, shotName, docsOnly, hover }) {
    log(`---- ${shotName}-${theme}${maximize ? ' (maximised)' : ''}${hover ? ' (hover)' : ''} ----`);
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({
        viewport: hover ? { width: 1600, height: 1100 } : { width: 1440, height: 900 },
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
    if (hover) {
        // The JSON viewer and the map share the response pane; the
        // default split gives the map a third. For this shot the map
        // gets the larger half — the ratio is the split pane's own
        // persisted preference.
        await page.evaluate(() => { try { localStorage.setItem('bowire_widget_split_ratio:kuestenlogik.maplibre', '0.44'); } catch (_) {} });
    }
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
    if (hover) {
        // Stop the stream so the viewer holds one message, then take
        // the route a click on the graphic takes: the coord-click event
        // scrolls the viewer to the vertex and expands its ancestors.
        // Hovering that vertex's latitude in the viewer is the JSON →
        // map direction: the graphic gets the accent under its stroke.
        const stopBtn = page.locator('.bowire-execute-btn.streaming-active').first();
        if (await stopBtn.isVisible().catch(() => false)) await stopBtn.click();
        await page.waitForTimeout(600);
        // The response pane over the whole width — the request pane
        // has nothing to show for a stream — so the viewer and the
        // map both get room.
        const maxResponse = page.locator('.bowire-pane-divider-edge-toggle-trailing').first();
        if (await maxResponse.isVisible().catch(() => false)) await maxResponse.click();
        await page.waitForTimeout(800);
        const vertex = await page.evaluate(() => {
            const h = (window.__bowireMapWidgets || []).filter((w) => w.container.isConnected)[0];
            const item = h.graphics().items.find((i) => /^[A-Z]{2}/.test(i.sidc));
            if (!item) return null;
            // The graphic's key is its points array; the last vertex,
            // so the code the line is drawn from — a few rows further
            // down — is in view with it.
            const parentPath = item.key + '[' + (item.points - 1) + ']';
            // The viewer keys its rows by the dot-only chain form.
            const chain = parentPath.replace(/^\$\./, '').replace(/\[(\d+)\]/g, '.$1');
            document.dispatchEvent(new CustomEvent('bowire:map-coord-click', {
                detail: { parentPath: chain, latPath: chain + '.latitudeCoordinate', lonPath: chain + '.longitudeCoordinate' }
            }));
            // The decorator stamps the pair's lat path in the bracket form.
            return parentPath.replace(/^\$\./, '') + '.latitudeCoordinate';
        });
        if (!vertex) throw new Error('no 2525C-coded graphic on the map — the sample should carry PL OSTSEE');
        await page.waitForTimeout(600);
        const span = page.locator(`[data-bowire-coord-path="${vertex}"]`).first();
        await span.waitFor({ state: 'visible', timeout: 10000 });
        await span.scrollIntoViewIfNeeded();
        await span.hover();
        await page.waitForTimeout(400);
        const lit = await page.evaluate(() =>
            (window.__bowireMapWidgets || []).filter((w) => w.container.isConnected)[0]
                .graphics().items.filter((i) => i.highlighted).map((i) => i.designation));
        log('  highlighted: ' + JSON.stringify(lit));
        if (lit.length !== 1) throw new Error('the hovered vertex should light exactly one graphic');
        // Closer in on the overlay: the fit that happened in the small
        // pane leaves the graphics a thumbnail in the widened one.
        await page.evaluate(() => {
            const h = (window.__bowireMapWidgets || []).filter((w) => w.container.isConnected)[0];
            h.flyTo({ center: [10.95, 54.17], zoom: 9.2, duration: 0 });
        });
        await page.waitForTimeout(500);
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
            if (process.env.ONLY_HOVER) break;
            await capture(theme, { maximize: false, shotName: 'tacticalapi-map', docsOnly: false });
        }
        if (THEMES.includes('dark')) {
            // The unsuffixed file is the dark variant — the convention every
            // screenshot pair under site/assets/images/screenshots follows.
            if (!process.env.ONLY_HOVER) for (const dir of [OUT, DOCS_OUT]) {
                fs.copyFileSync(path.join(dir, 'tacticalapi-map-dark.png'), path.join(dir, 'tacticalapi-map.png'));
            }
            // The widget doc's hero: the map alone, downscaled — a 2x
            // satellite frame is ~5 MB as PNG, 1600 px wide is a third.
            const sharp = require('sharp');
            if (!process.env.ONLY_HOVER) {
            const shot = await capture('dark', { maximize: true, shotName: 'map-widget-pins', docsOnly: true });
            const hero = path.join(DOCS_OUT, 'map-widget-pins.png');
            const buf = await sharp(shot).resize({ width: 1600 }).png({ compressionLevel: 9 }).toBuffer();
            fs.writeFileSync(hero, buf);
            fs.unlinkSync(shot);
            log(`  -> ${hero}`);
            }
            // The hover-sync between the JSON viewer and a graphic, and
            // the 2525C code in the viewer next to the line it draws as.
            const hoverShot = await capture('dark', { hover: true, shotName: 'map-widget-graphics-hover', docsOnly: true });
            const hoverOut = path.join(DOCS_OUT, 'map-widget-graphics-hover.png');
            const hoverBuf = await sharp(hoverShot).resize({ width: 1600 }).png({ compressionLevel: 9 }).toBuffer();
            fs.writeFileSync(hoverOut, hoverBuf);
            fs.unlinkSync(hoverShot);
            log(`  -> ${hoverOut}`);
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
