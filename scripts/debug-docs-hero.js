const { chromium } = require('playwright');

(async () => {
    const browser = await chromium.launch();
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    await page.goto('http://127.0.0.1:4001/Bowire/docs/', { waitUntil: 'networkidle' });
    await page.waitForTimeout(400);

    await page.screenshot({ path: '/tmp/docs-index.png', fullPage: false });

    const m = await page.evaluate(() => {
        const pick = (sel) => {
            const el = document.querySelector(sel);
            if (!el) return null;
            const r = el.getBoundingClientRect();
            const cs = getComputedStyle(el);
            return {
                left: Math.round(r.left), right: Math.round(r.right), width: Math.round(r.width),
                marginLeft: cs.marginLeft, marginRight: cs.marginRight,
                textAlign: cs.textAlign, maxWidth: cs.maxWidth,
            };
        };
        return {
            viewport: window.innerWidth,
            hero: pick('.bowire-docs-hero'),
            heroTitle: pick('.bowire-docs-hero-title'),
            heroTagline: pick('.bowire-docs-hero-tagline'),
            heroLogo: pick('.bowire-docs-hero-logo'),
            content: pick('main.bowire-docs-main-shell > .content'),
        };
    });

    console.log(JSON.stringify(m, null, 2));
    await browser.close();
})();
