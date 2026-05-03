const { chromium } = require('playwright');

(async () => {
    const browser = await chromium.launch();
    const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } });
    await page.goto('http://127.0.0.1:4001/Bowire/quickstart.html', { waitUntil: 'networkidle' });
    await page.waitForTimeout(400);

    // Don't click any path — keep the pending placeholder visible.
    // Scroll to it.
    await page.evaluate(() => {
        const el = document.querySelector('[data-step-pending]');
        if (el) el.scrollIntoView({ block: 'center' });
    });
    await page.waitForTimeout(300);

    await page.screenshot({ path: '/tmp/quickstart-pending.png', fullPage: false });
    console.log('saved');
    await browser.close();
})();
