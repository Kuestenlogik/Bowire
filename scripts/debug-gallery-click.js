const { chromium } = require('@playwright/test');
const path = require('path');
(async () => {
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    const page = await ctx.newPage();
    await page.goto('http://localhost:4000/', { waitUntil: 'networkidle' });
    await page.waitForTimeout(800);
    await page.locator('.screenshot-carousel').scrollIntoViewIfNeeded();
    await page.waitForTimeout(400);

    // Click first dot, capture
    await page.locator('.screenshot-dot').nth(0).click();
    await page.waitForTimeout(800);
    await page.screenshot({ path: path.resolve(__dirname, '..', 'site', 'assets', 'videos', 'debug-dot1.png') });

    // Click middle dot
    await page.locator('.screenshot-dot').nth(6).click();
    await page.waitForTimeout(800);
    await page.screenshot({ path: path.resolve(__dirname, '..', 'site', 'assets', 'videos', 'debug-dot-mid.png') });

    // Click last dot
    await page.locator('.screenshot-dot').last().click();
    await page.waitForTimeout(800);
    await page.screenshot({ path: path.resolve(__dirname, '..', 'site', 'assets', 'videos', 'debug-dot-last.png') });

    console.log('Saved debug-dot1 / debug-dot-mid / debug-dot-last');
    await browser.close();
})();
