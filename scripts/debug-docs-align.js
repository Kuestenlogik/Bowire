// Quick capture for docs alignment debugging — full-page screenshot
// of /docs/setup/index.html plus the bounding rectangles of the
// github icon (top-right of the header) and the affix sidebar
// (right column with "In this article"). The right edges should
// match — any delta is the alignment bug.

const { chromium } = require('playwright');

(async () => {
    const browser = await chromium.launch();
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    await page.goto('http://127.0.0.1:4001/Bowire/docs/setup/index.html', {
        waitUntil: 'networkidle',
    });
    await page.waitForTimeout(500);

    const out = '/tmp/docs-setup.png';
    await page.screenshot({ path: out, fullPage: false });

    const measure = await page.evaluate(() => {
        const pick = (sel) => {
            const el = document.querySelector(sel);
            if (!el) return null;
            const r = el.getBoundingClientRect();
            return {
                left: Math.round(r.left),
                right: Math.round(r.right),
                width: Math.round(r.width),
            };
        };
        return {
            viewport: window.innerWidth,
            header: pick('.bowire-docs-header-inner'),
            github: pick('.bowire-docs-nav-github'),
            actions: pick('.bowire-docs-header-actions'),
            main: pick('main.bowire-docs-main-shell'),
            toc: pick('.toc-offcanvas'),
            content: pick('main.bowire-docs-main-shell > .content'),
            affix: pick('.affix'),
        };
    });

    console.log('screenshot:', out);
    console.log(JSON.stringify(measure, null, 2));

    await browser.close();
})();
