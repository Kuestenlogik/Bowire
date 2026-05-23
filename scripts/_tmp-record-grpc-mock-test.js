// Records a single SchedulePortCall gRPC unary roundtrip via the
// bowire workbench, then prints the resulting recording JSON to
// stdout so an orchestration shell can write it to disk for
// `bowire mock --recording <file>`.
const { chromium } = require('@playwright/test');

const BOWIRE_URL = process.env.BOWIRE_URL || 'http://localhost:5202/';

(async () => {
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({
        viewport: { width: 1440, height: 900 },
        ignoreHTTPSErrors: true,
        colorScheme: 'dark',
    });
    const page = await ctx.newPage();
    page.on('console', (m) => {
        if (m.type() === 'error') console.error('[browser]', m.text());
    });
    await page.goto(BOWIRE_URL, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
    await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 15000 });

    // Expand all service groups so methods are clickable.
    for (const grp of await page.locator('.bowire-service-group').all()) {
        const chev = grp.locator('.bowire-service-chevron').first();
        const cls = await chev.getAttribute('class').catch(() => '');
        if (!cls || !cls.includes('expanded')) {
            await grp.locator('.bowire-service-header').first().click().catch(() => {});
            await page.waitForTimeout(80);
        }
    }
    await page.waitForTimeout(400);

    // Pick the gRPC unary method we want to record.
    const method = page.locator('.bowire-method-item', { hasText: 'SchedulePortCall' }).first();
    await method.click();
    await page.waitForTimeout(800);

    // Toggle recording ON before the call so the response gets captured.
    const start = page.locator('#bowire-recording-start-btn').first();
    await start.click();
    await page.waitForTimeout(800);
    console.log('record: armed');

    // Replace the default empty request with a real-looking SchedulePortCallRequest
    // so the server returns a meaningful response.
    const jsonInput = page.locator('.cm-content, .bowire-json-editor textarea, textarea[name="message"], [contenteditable="true"]').first();
    if (await jsonInput.isVisible().catch(() => false)) {
        await jsonInput.click();
        await page.keyboard.press('Control+A');
        await page.keyboard.type(JSON.stringify({
            shipId: 1,
            dockNumber: 3,
            scheduledArrival: '2026-06-01T10:00:00Z',
            cargoOperation: 'Loading',
        }));
        await page.waitForTimeout(300);
    }

    // Fire the call.
    const exec = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
    await exec.click();
    // Wait for the response card / json tree.
    await page.waitForSelector('.bowire-response-output.is-interactive, .bowire-response-output.error, .bowire-json-tree', { timeout: 15000 });
    await page.waitForTimeout(1200);
    console.log('record: response landed');

    // Stop recording.
    const stop = page.locator('#bowire-recording-stop-btn').first();
    await stop.click();
    await page.waitForTimeout(1500);
    console.log('record: stopped');

    // Extract the latest recording. We hit the bowire backend's
    // /api/recordings endpoint directly so we get whatever the disk
    // sync has — that's what the mock will read too.
    const recordings = await page.evaluate(async () => {
        const r = await fetch('/api/recordings');
        return r.ok ? r.json() : { error: r.status };
    });

    if (recordings && recordings.recordings && recordings.recordings.length > 0) {
        // Pick the most recent recording with at least one step.
        const withSteps = recordings.recordings.filter(r => r.steps && r.steps.length > 0);
        const latest = withSteps.length > 0
            ? withSteps[withSteps.length - 1]
            : recordings.recordings[recordings.recordings.length - 1];
        console.log('---RECORDING-JSON-BEGIN---');
        console.log(JSON.stringify(latest, null, 2));
        console.log('---RECORDING-JSON-END---');
    } else {
        console.error('no recordings returned:', recordings);
    }

    await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
