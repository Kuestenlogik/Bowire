/**
 * Captures the TacticalAPI popup-gallery screenshots — the
 * tacticalapi-map-{dark,light}.png pair that the protocols.html popup
 * cross-fades to when the user clicks the second gallery thumbnail.
 *
 * Spins up the TacticalAPI sample server (Kuestenlogik.Bowire.Samples.TacticalApi)
 * at :5120, drives the embedded /bowire workbench via Playwright,
 * picks SubscribeSituationObjectEvents, lets the seeded snapshot stream
 * three frames, then shoots split-pane (JSON tree left, MapLibre map
 * right) for both themes.
 *
 * The seeded scene is intentionally framed for the Wismarer Bucht —
 * friend cyan rectangle near Wismar harbour, hostile red diamond
 * north of Insel Poel, neutral green square mid-channel. The basemap
 * is forced to ESRI World Imagery (satellite alias) via a setter trap
 * on `window.__BOWIRE_CONFIG__` so the screenshot picks up the aerial
 * tiles instead of the offline demotiles default.
 *
 * Run:
 *   node scripts/capture-tacticalapi-screenshot.js          # both themes
 *   THEME=dark node scripts/capture-tacticalapi-screenshot.js
 *   THEME=light node scripts/capture-tacticalapi-screenshot.js
 *
 * Skips the sample-server spawn when --no-spawn is passed (lets you
 * point at an already-running instance during local iteration).
 */
const { chromium } = require('@playwright/test');
const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');
const https = require('https');

const SAMPLE_DIR = path.resolve(
    __dirname, '..', '..', 'Bowire.Samples', 'src',
    'Kuestenlogik.Bowire.Samples.TacticalApi');
const PLUGIN_REPO = path.resolve(
    __dirname, '..', '..', 'Bowire.Protocol.TacticalApi', 'src',
    'Kuestenlogik.Bowire.Protocol.TacticalApi');
const PLUGIN_STAGING = path.resolve(
    __dirname, '..', 'artifacts', 'capture-plugin-staging');
const BOWIRE_CLI = path.resolve(
    __dirname, '..', 'artifacts', 'bin',
    'Kuestenlogik.Bowire.Tool', 'Release', 'bowire.dll');

// Sample-server hosts the Situation gRPC service at :5120 — no
// reflection, typed discovery via the plugin only. The bowire CLI
// runs on :5079 with the TacticalAPI plugin loaded, pointed at the
// sample via the `tacticalapi@…` URL hint.
const SAMPLE_GRPC_URL = 'https://localhost:5120';
const BOWIRE_UI_URL = 'http://localhost:5079/';
const SAMPLE_HEALTH_URL = 'https://localhost:5120/bowire'; // sample mounts its own no-op bowire too
const OUT = path.resolve(__dirname, '..', 'site', 'assets', 'images', 'screenshots');
const DOCS_OUT = path.resolve(__dirname, '..', 'docs', 'images', 'screenshots');
for (const dir of [OUT, DOCS_OUT]) {
    if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
}

const themesArg = (process.env.THEME || '').toLowerCase();
const THEMES = themesArg ? [themesArg] : ['dark', 'light'];
const SPAWN_SAMPLE = !process.argv.includes('--no-spawn');

function log(m) { console.log(new Date().toISOString().slice(11, 19), m); }

/**
 * Poll a URL until it returns < 500. Self-signed dev certs accepted via
 * a localhost-scoped https.Agent. Works for both http:// (bowire CLI)
 * and https:// (sample server with Kestrel dev cert).
 */
function waitForHealth(url, timeoutMs) {
    const isHttps = url.startsWith('https:');
    const transport = isHttps ? https : require('http');
    const agentOpts = isHttps
        ? { agent: new https.Agent({ rejectUnauthorized: false }) }
        : {};
    return new Promise((resolve, reject) => {
        const deadline = Date.now() + timeoutMs;
        const tick = () => {
            const req = transport.get(url, { ...agentOpts, timeout: 2000 }, (res) => {
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

/**
 * Publish the TacticalAPI plugin into a staging plugin-dir so the
 * bowire CLI can load it via `--plugin-dir`. PluginManager scans for
 * sub-directories, so the layout must be
 *
 *   staging/Kuestenlogik.Bowire.Protocol.TacticalApi/
 *     Kuestenlogik.Bowire.Protocol.TacticalApi.dll
 *     (transitive deps: Grpc.*, Google.Protobuf, …)
 *
 * `dotnet publish -o` writes that exact shape — no extra wiring needed.
 * Skipping the step is fine when an earlier run already populated the
 * dir.
 */
function publishPlugin() {
    return new Promise((resolve, reject) => {
        const subDir = path.join(PLUGIN_STAGING, 'Kuestenlogik.Bowire.Protocol.TacticalApi');
        const dll = path.join(subDir, 'Kuestenlogik.Bowire.Protocol.TacticalApi.dll');
        if (fs.existsSync(dll)) {
            log(`Plugin already staged at ${subDir}`);
            return resolve();
        }
        log('Publishing TacticalAPI plugin into staging dir…');
        fs.mkdirSync(subDir, { recursive: true });
        const proc = spawn(
            'dotnet',
            ['publish', PLUGIN_REPO, '-c', 'Release', '--nologo', '-o', subDir],
            { stdio: ['ignore', 'inherit', 'inherit'], shell: process.platform === 'win32' }
        );
        proc.on('exit', (code) => {
            if (code === 0) resolve();
            else reject(new Error('plugin publish failed with code ' + code));
        });
    });
}

async function capture(theme) {
    const shotName = 'tacticalapi-map';
    log(`---- ${shotName}-${theme} ----`);
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({
        viewport: { width: 1440, height: 900 },
        deviceScaleFactor: 2,
        ignoreHTTPSErrors: true,
        colorScheme: theme === 'light' ? 'light' : 'dark',
    });

    const page = await ctx.newPage();
    // Surface browser-side errors — fetchServices failures, MapLibre
    // load issues, the extension framework's "kind not registered"
    // warnings — so a Playwright timeout below has actionable context
    // instead of just "selector never appeared."
    page.on('console', (msg) => {
        if (msg.type() === 'error' || msg.type() === 'warning') {
            log(`  [page ${msg.type()}] ${msg.text()}`);
        }
    });
    page.on('pageerror', (err) => log(`  [page error] ${err.message}`));
    page.on('requestfailed', (req) => log(`  [req fail] ${req.url()} → ${req.failure()?.errorText}`));

    await page.goto(BOWIRE_UI_URL, { waitUntil: 'domcontentloaded' });
    await page.evaluate((t) => {
        try { localStorage.setItem('bowire-theme', t); } catch (_) {}
    }, theme);
    // Reload so the localStorage theme takes effect on first paint.
    // The setter trap survives reload because addInitScript reruns on
    // every navigation.
    await page.reload({ waitUntil: 'domcontentloaded' });

    try {
        await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 30000 });
    } catch (e) {
        // Dump page state before throwing so the error message points
        // at the actual cause (e.g. "sidebar empty: service-list shows
        // 'connecting to https://localhost:5120…'" or whatever).
        const bodyText = await page.locator('body').innerText().catch(() => '<no body>');
        log(`  sidebar timeout — visible page text:\n${bodyText.substring(0, 800)}`);
        await page.screenshot({ path: path.join(OUT, `debug-${theme}.png`) }).catch(() => {});
        throw e;
    }
    log('  sidebar populated');

    // Expand any collapsed service groups so the target method's row
    // ends up visible (Bowire keeps groups collapsed when there are
    // > 5 services; TacticalAPI's Situation service is the only one
    // here, but the expand pass is harmless when already open and is
    // copy-paste consistent with capture-builtin-screenshots.js).
    const headers = await page.locator('.bowire-service-header').all();
    for (const h of headers) {
        const expanded = await h.locator('.bowire-service-chevron.expanded').count();
        if (expanded === 0) await h.click().catch(() => {});
    }
    await page.waitForTimeout(300);

    await page.locator('.bowire-method-item', { hasText: 'SubscribeSituationObjectEvents' })
        .first()
        .click();
    await page.waitForTimeout(500);

    await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
    log('  stream started — waiting for pins to land');

    // Wait for the map canvas to attach (the widget mounts inside the
    // split-pane host once the framework's pairing resolution finds
    // coordinate.{latitude,longitude} on the response shape).
    await page.waitForFunction(() => {
        return document.querySelector('.maplibregl-canvas') != null;
    }, null, { timeout: 20000 });
    log('  map canvas attached');

    // Give MapLibre time to load the satellite tiles + run the
    // icon-image sprite registrations + render the symbol layer for
    // every streamed frame. The seeded snapshot ships three objects
    // back-to-back; 4.5s comfortably covers the tile round-trip on a
    // fresh page load.
    await page.waitForTimeout(4500);

    const file = path.join(OUT, `${shotName}-${theme}.png`);
    await page.screenshot({ path: file, fullPage: false });
    fs.copyFileSync(file, path.join(DOCS_OUT, `${shotName}-${theme}.png`));
    log(`  -> ${file}`);

    // Stop the stream cleanly so the next browser context doesn't
    // collide with a still-open SSE channel on the sample server.
    const cancelBtn = page.locator('.bowire-cancel-btn, #bowire-cancel-btn').first();
    if (await cancelBtn.isVisible().catch(() => false)) {
        await cancelBtn.click().catch(() => {});
    }

    await browser.close();
}

(async () => {
    let sampleProc = null;
    let bowireProc = null;
    if (SPAWN_SAMPLE) {
        await publishPlugin();

        log('Starting TacticalAPI sample server (gRPC :5120)…');
        sampleProc = spawn(
            'dotnet',
            ['run', '--project', SAMPLE_DIR, '-c', 'Release', '--no-build'],
            {
                stdio: ['ignore', 'inherit', 'inherit'],
                shell: process.platform === 'win32',
            }
        );
        sampleProc.on('exit', (code, sig) => {
            log(`Sample server exited (code=${code} signal=${sig})`);
        });
        log('Waiting for sample on :5120…');
        await waitForHealth(SAMPLE_HEALTH_URL, 90000);
        log('Sample ready.');

        log('Starting Bowire CLI (UI :5079) with TacticalAPI plugin + sample URL…');
        const stagedPluginDir = PLUGIN_STAGING;
        bowireProc = spawn(
            'dotnet',
            [BOWIRE_CLI,
             '--port', '5079',
             '--no-browser',
             '--plugin-dir', stagedPluginDir,
             '--url', `tacticalapi@${SAMPLE_GRPC_URL}`,
             // --map-basemap satellite lands in __BOWIRE_CONFIG__.mapBasemap
             // server-side via BowireOptions.MapBasemap. Replaces the
             // earlier client-side setter-trap on window.__BOWIRE_CONFIG__
             // — now the same config knob that an operator would use in
             // appsettings.json drives the capture.
             '--map-basemap', 'satellite'],
            {
                stdio: ['ignore', 'inherit', 'inherit'],
                shell: process.platform === 'win32',
                env: {
                    ...process.env,
                    // Sample server uses Kestrel's self-signed dev cert
                    // on https://localhost:5120. Without this flag the
                    // plugin's HttpClient rejects the cert and discovery
                    // returns 502.
                    Bowire__TrustLocalhostCert: 'true',
                },
            }
        );
        bowireProc.on('exit', (code, sig) => {
            log(`Bowire CLI exited (code=${code} signal=${sig})`);
        });
        log('Waiting for bowire workbench on :5079…');
        await waitForHealth(BOWIRE_UI_URL, 60000);
        log('Workbench ready.');
    } else {
        log('Skipping spawn (--no-spawn); expecting workbench on :5079 already');
        await waitForHealth(BOWIRE_UI_URL, 5000);
    }

    try {
        for (const theme of THEMES) {
            await capture(theme);
        }
        // The unsuffixed tacticalapi-map.png is the same image as the
        // dark variant — matches the convention every other screenshot
        // pair in site/assets/images/screenshots/ follows. Only copy
        // when both variants were just captured.
        if (THEMES.includes('dark')) {
            for (const dir of [OUT, DOCS_OUT]) {
                fs.copyFileSync(
                    path.join(dir, 'tacticalapi-map-dark.png'),
                    path.join(dir, 'tacticalapi-map.png')
                );
            }
            log('  -> tacticalapi-map.png (dark copy)');
        }
    } finally {
        for (const [name, proc] of [['bowire CLI', bowireProc], ['sample server', sampleProc]]) {
            if (!proc) continue;
            log(`Shutting down ${name}…`);
            try { proc.kill('SIGTERM'); } catch (_) {}
            // Force-kill backstop in case dotnet ignores SIGTERM
            // (Windows is notoriously inconsistent about graceful
            // shutdown signals from child processes).
            setTimeout(() => { try { proc.kill('SIGKILL'); } catch (_) {} }, 5000);
        }
    }
})().catch(err => { console.error(err); process.exit(1); });
