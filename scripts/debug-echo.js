/**
 * Quick debug: open Bowire, connect to Echo, send two messages,
 * screenshot the WebSocket stream UI.
 */
const { chromium } = require('@playwright/test');
const path = require('path');

(async () => {
    const browser = await chromium.launch({ headless: true });
    const context = await browser.newContext({
        viewport: { width: 1280, height: 720 },
        ignoreHTTPSErrors: true
    });
    const page = await context.newPage();

    await page.goto('https://localhost:5101/bowire', { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 15000 });
    await page.waitForSelector('.bowire-method-item', { timeout: 20000 });
    await page.waitForTimeout(1000);

    // Expand all groups
    for (const g of await page.locator('.bowire-service-group.collapsed .bowire-service-group-header').all()) {
        await g.click().catch(() => {});
    }

    // Click Echo
    const echo = page.locator('.bowire-method-item', { hasText: 'Echo' }).first();
    await echo.click();
    await page.waitForTimeout(1500);

    // Connect if needed
    const connectBtn = page.locator('#bowire-channel-connect-btn').first();
    if (await connectBtn.isVisible().catch(() => false)) {
        await connectBtn.click();
        await page.waitForTimeout(1500);
    }

    // Fill + send twice
    const input = page.locator('input[data-field-key="data"]').first();
    const sendBtn = page.locator('#bowire-channel-send-btn').first();

    await input.click();
    await input.fill('Hello from Bowire');
    await page.waitForTimeout(300);
    await sendBtn.click();
    await page.waitForTimeout(1200);

    await input.fill('Streaming works both ways');
    await page.waitForTimeout(300);
    await sendBtn.click();
    await page.waitForTimeout(1500);

    const out = path.resolve(__dirname, '..', 'site', 'assets', 'videos', 'debug-echo.png');
    await page.screenshot({ path: out, fullPage: false });
    console.log('Saved', out);

    await context.close();
    await browser.close();
})();
