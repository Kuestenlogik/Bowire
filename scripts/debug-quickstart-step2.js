const { chromium } = require('playwright');

(async () => {
    const browser = await chromium.launch();
    const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } });
    await page.goto('http://127.0.0.1:4001/Bowire/quickstart.html', { waitUntil: 'networkidle' });
    await page.waitForTimeout(400);

    // Pick the standalone path so step 2 reveals
    await page.click('[data-path="standalone"]');
    await page.waitForTimeout(300);

    // Scroll to step 2 (Install Bowire)
    await page.evaluate(() => {
        const el = document.querySelector('#install');
        if (el) el.scrollIntoView({ block: 'start' });
    });
    await page.waitForTimeout(300);

    await page.screenshot({ path: '/tmp/quickstart-step2.png', fullPage: false });
    console.log('saved');
    await browser.close();
})();
