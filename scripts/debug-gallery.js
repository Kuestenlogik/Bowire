const { chromium } = require('@playwright/test');
const path = require('path');
(async () => {
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    const page = await ctx.newPage();
    await page.goto('http://localhost:4000/', { waitUntil: 'networkidle' });
    await page.waitForTimeout(1200);
    // Scroll to the gallery section
    await page.locator('.screenshot-carousel').scrollIntoViewIfNeeded();
    await page.waitForTimeout(500);
    // Full-viewport screenshot with only the gallery visible
    await page.screenshot({ path: path.resolve(__dirname, '..', 'site', 'assets', 'videos', 'debug-gallery.png') });
    console.log('Saved debug-gallery.png');
    await browser.close();
})();
