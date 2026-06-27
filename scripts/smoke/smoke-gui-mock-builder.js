/**
 * Playwright end-to-end smoke for the GUI Mock Builder.
 *
 * Three phases, all driven from one process:
 *   1. Boot the `bowire` CLI workbench on a free port, navigate
 *      Chromium to it, click through + → REST freeform → fill
 *      service/method/body → expand Mock Response → type mock body +
 *      status → Save as Mock Step. Assert via /api/recordings that a
 *      step landed with the expected fields.
 *   2. Pull the freshly-added step from the store, write it as a
 *      single-recording JSON file, and launch a second `bowire mock`
 *      process against it on its own port.
 *   3. Fetch GET <mockport>/users/42 and assert the body matches the
 *      mock response the user authored in the UI.
 *
 * If any phase fails the script drops a screenshot + HTML dump under
 * artifacts/gui-mock-smoke/ for triage.
 *
 * Run from the repo root:
 *     cd C:/Projekte/KL/Bowire
 *     node scripts/smoke-gui-mock-builder.js
 */

const { chromium } = require('@playwright/test');
const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');
const net = require('net');

const REPO_ROOT = path.resolve(__dirname, '..');
const DEBUG_DIR = path.join(REPO_ROOT, 'artifacts', 'gui-mock-smoke');

function log(msg) { console.log(new Date().toISOString().slice(11, 19), msg); }
function die(msg) { console.error('FAIL:', msg); process.exit(1); }

async function pickFreePort() {
    return new Promise((resolve, reject) => {
        const srv = net.createServer();
        srv.unref();
        srv.on('error', reject);
        srv.listen(0, () => {
            const { port } = srv.address();
            srv.close(() => resolve(port));
        });
    });
}

async function waitForHttp(url, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        try {
            const r = await fetch(url);
            if (r.status >= 200 && r.status < 500) return;
        } catch { /* not up yet */ }
        await new Promise(r => setTimeout(r, 300));
    }
    throw new Error(`timed out waiting for ${url}`);
}

async function dumpDebug(page, tag) {
    fs.mkdirSync(DEBUG_DIR, { recursive: true });
    try { await page.screenshot({ path: path.join(DEBUG_DIR, `${tag}.png`), fullPage: true }); } catch {}
    try { fs.writeFileSync(path.join(DEBUG_DIR, `${tag}.html`), await page.content()); } catch {}
    log(`  debug dump: ${DEBUG_DIR}/${tag}.{png,html}`);
}

(async () => {
    const port = await pickFreePort();
    const baseUrl = `http://localhost:${port}`;

    // Use a throw-away HOME so the test doesn't touch the user's
    // ~/.bowire/recordings.json. The CLI honours HOME via
    // ResolveBowireDir → Environment.GetFolderPath(UserProfile).
    const testHome = path.join(REPO_ROOT, 'artifacts', 'gui-mock-smoke-home');
    fs.rmSync(testHome, { recursive: true, force: true });
    fs.mkdirSync(testHome, { recursive: true });

    log(`Launching bowire on :${port} (HOME=${testHome})`);
    const env = Object.assign({}, process.env, {
        HOME: testHome,
        USERPROFILE: testHome
    });
    const cli = spawn('dotnet', [
        'run', '--project',
        path.join(REPO_ROOT, 'src/Kuestenlogik.Bowire.Tool'),
        '--no-build', '--',
        '--port', String(port)
    ], { cwd: REPO_ROOT, env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
    cli.stdout.on('data', () => {});
    cli.stderr.on('data', () => {});

    let browser = null;
    let mockProc = null;
    let exitCode = 0;

    try {
        await waitForHttp(`${baseUrl}/bowire`, 30_000);
        log('  workbench reachable');

        browser = await chromium.launch({ headless: true });
        const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
        const page = await context.newPage();
        page.on('pageerror', e => log(`[browser ERR] ${e.message}`));

        await page.goto(`${baseUrl}/bowire`, { waitUntil: 'domcontentloaded' });
        await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 15_000 });
        log('  app ready');

        // Dismiss auto-tour if present.
        const skip = page.locator('.bowire-tour-skip, .bowire-tour-close, button:has-text("Skip")').first();
        if (await skip.isVisible().catch(() => false)) {
            await skip.click().catch(() => {});
            await page.waitForTimeout(200);
        }

        // Open "+" dropdown → REST.
        log('  opening + dropdown → REST freeform');
        await page.click('#bowire-new-btn');
        await page.waitForSelector('.bowire-new-dropdown', { timeout: 2_000 });
        await page.locator('.bowire-new-dropdown-item', { hasText: 'REST' }).first().click();
        await page.waitForSelector('#bowire-freeform-execute-btn', { timeout: 5_000 });

        // Type service + method.
        await page.fill('#bowire-freeform-service-input', 'Users');
        await page.fill('#bowire-freeform-method-input', 'GET /users/42');

        // Expand Mock Response section + type mock data.
        log('  expanding Mock Response, typing response body');
        await page.locator('.bowire-freeform-mock-toggle').click();
        await page.waitForSelector('#bowire-freeform-mock-body', { timeout: 2_000 });
        await page.fill('#bowire-freeform-mock-status-input', 'OK');
        await page.fill('#bowire-freeform-mock-body', '{"id":42,"name":"Ada"}');

        // Save as Mock Step.
        log('  clicking Save as Mock Step');
        await page.click('#bowire-freeform-save-mock-btn');
        await page.waitForTimeout(500);

        // Wait for the debounced disk-sync (400ms) + a little slack.
        await page.waitForTimeout(900);

        // Assert via the server-side API that a step landed. The
        // `/api/recordings` route is prefix-scoped, matching the
        // workbench mount path (`/bowire` by default).
        const rec = await fetch(`${baseUrl}/bowire/api/recordings`).then(r => r.json());
        const recordings = rec.recordings || [];
        log(`  /api/recordings has ${recordings.length} recording(s)`);
        if (recordings.length === 0) {
            await dumpDebug(page, 'no-recording');
            die('expected a "Manual mocks" recording to have been created');
        }

        const manual = recordings.find(r => r.name === 'Manual mocks');
        if (!manual) {
            await dumpDebug(page, 'no-manual-mocks');
            die('expected a recording named "Manual mocks"');
        }
        if (!manual.steps || manual.steps.length === 0) {
            await dumpDebug(page, 'no-steps');
            die('expected at least one step in the Manual mocks recording');
        }

        const step = manual.steps[0];
        const checks = [
            ['protocol', 'rest'],
            ['service', 'Users'],
            ['method', 'GET /users/42'],
            ['httpVerb', 'GET'],
            ['httpPath', '/users/42'],
            ['status', 'OK']
        ];
        for (const [field, expected] of checks) {
            if (step[field] !== expected) {
                await dumpDebug(page, `bad-${field}`);
                die(`step.${field}: expected "${expected}", got "${step[field]}"`);
            }
        }
        const body = step.response || '';
        if (!body.includes('"name":"Ada"')) {
            await dumpDebug(page, 'bad-response');
            die(`step.response did not contain the authored mock body: ${body}`);
        }

        log('OK — GUI Mock Builder recording step shape verified');

        // -----------------------------------------------------------
        // Phase 3 — spin up `bowire mock` against the step we just
        // authored and assert the wire traffic matches. Closes the
        // true GUI-built-mock → replay contract.
        // -----------------------------------------------------------
        fs.mkdirSync(DEBUG_DIR, { recursive: true });
        const singleRec = {
            id: manual.id,
            name: manual.name,
            recordingFormatVersion: manual.recordingFormatVersion || 2,
            createdAt: manual.createdAt || Date.now(),
            steps: [step]   // just the one we assert against; keeps matching unambiguous
        };
        const recPath = path.join(DEBUG_DIR, 'gui-mock-step.json');
        fs.writeFileSync(recPath, JSON.stringify(singleRec, null, 2));
        log(`  wrote single-step recording → ${recPath}`);

        const mockPort = await pickFreePort();
        log(`  launching 'bowire mock' on :${mockPort}`);
        mockProc = spawn('dotnet', [
            'run', '--project',
            path.join(REPO_ROOT, 'src/Kuestenlogik.Bowire.Tool'),
            '--no-build', '--',
            'mock', '--recording', recPath,
            '--port', String(mockPort), '--no-watch'
        ], { cwd: REPO_ROOT, env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
        mockProc.stdout.on('data', () => {});
        mockProc.stderr.on('data', () => {});

        const mockBaseUrl = `http://localhost:${mockPort}`;
        await waitForHttp(`${mockBaseUrl}/users/42`, 30_000);

        const wireResp = await fetch(`${mockBaseUrl}/users/42`);
        const wireBody = await wireResp.text();
        log(`  GET ${mockBaseUrl}/users/42 → ${wireResp.status} (${wireBody.length} bytes)`);
        if (wireResp.status !== 200) {
            die(`mock returned ${wireResp.status}; expected 200`);
        }
        if (!wireBody.includes('"name":"Ada"') || !wireBody.includes('"id":42')) {
            die(`wire body did not match the authored mock: ${wireBody}`);
        }

        log('OK — GUI-built mock replays the authored response end-to-end');
    } catch (e) {
        log(`FAIL: ${e.message}`);
        exitCode = 1;
    } finally {
        if (browser) { try { await browser.close(); } catch {} }
        function killTree(proc) {
            if (!proc) return;
            try {
                if (process.platform === 'win32') {
                    spawn('taskkill', ['/F', '/T', '/PID', String(proc.pid)], { stdio: 'ignore' });
                } else {
                    proc.kill('SIGTERM');
                }
            } catch {}
        }
        killTree(cli);
        killTree(mockProc);
    }
    process.exit(exitCode);
})();
