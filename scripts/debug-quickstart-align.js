const { chromium } = require('playwright');

(async () => {
    const browser = await chromium.launch();
    const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } });
    await page.goto('http://127.0.0.1:4001/Bowire/quickstart.html', { waitUntil: 'networkidle' });
    await page.click('[data-path="standalone"]');
    await page.waitForTimeout(300);

    const m = await page.evaluate(() => {
        const out = [];
        const steps = document.querySelectorAll('.quickstart-page-step:not([hidden])');
        steps.forEach((s) => {
            const bubble = s.querySelector('.quickstart-page-step-num');
            const body   = s.querySelector('.quickstart-page-step-body');
            const head   = body && (body.querySelector('h2') || body.querySelector('p'));
            if (!bubble || !head) return;
            const br = bubble.getBoundingClientRect();
            const hr = head.getBoundingClientRect();
            const cs = getComputedStyle(head);
            out.push({
                step: (s.querySelector('h2') || {}).textContent || '(pending)',
                bubbleCenterY: Math.round((br.top + br.bottom) / 2),
                headFirstLineCenterY: Math.round(hr.top + parseFloat(cs.lineHeight) / 2),
                lineHeight: cs.lineHeight,
                fontSize: cs.fontSize,
                bodyPaddingTop: getComputedStyle(body).paddingTop,
            });
        });
        return out;
    });
    console.log(JSON.stringify(m, null, 2));
    await browser.close();
})();
