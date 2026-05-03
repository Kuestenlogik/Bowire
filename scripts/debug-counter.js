/**
 * Quick debug: open Bowire, run Counter, screenshot the stream UI.
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

    // Click Counter
    const counter = page.locator('.bowire-method-item', { hasText: 'Counter' }).first();
    await counter.click();
    await page.waitForTimeout(1200);

    // Fill params
    await page.locator('input[data-field-key="count"]').first().fill('5');
    await page.locator('input[data-field-key="delayMs"]').first().fill('200');
    await page.waitForTimeout(300);

    // Execute
    await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
    await page.waitForTimeout(2500); // let all 5 arrive

    const out = path.resolve(__dirname, '..', 'site', 'assets', 'videos', 'debug-counter.png');
    await page.screenshot({ path: out, fullPage: false });
    console.log('Saved', out);

    await context.close();
    await browser.close();
})();
