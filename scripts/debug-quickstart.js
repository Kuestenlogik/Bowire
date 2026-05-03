const { chromium } = require('playwright');

(async () => {
    const browser = await chromium.launch();
    const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } });
    await page.goto('http://127.0.0.1:4001/Bowire/quickstart.html', {
        waitUntil: 'networkidle',
    });
    await page.waitForTimeout(500);
    await page.screenshot({ path: '/tmp/quickstart.png', fullPage: true });
    console.log('saved');
    await browser.close();
})();
