const { chromium } = require('playwright');

(async () => {
    const browser = await chromium.launch();
    const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } });
    await page.goto('http://127.0.0.1:4001/Bowire/quickstart.html', { waitUntil: 'networkidle' });
    await page.waitForTimeout(400);

    const m = await page.evaluate(() => {
        const article = document.querySelector('[data-step-pending]');
        const bubble = article && article.querySelector('.quickstart-page-step-num');
        const body   = article && article.querySelector('.quickstart-page-step-body');
        const p      = body && body.querySelector('p');
        if (!article || !bubble || !body || !p) return { ok: false };
        const ar = article.getBoundingClientRect();
        const br = bubble.getBoundingClientRect();
        const bdr = body.getBoundingClientRect();
        const pr = p.getBoundingClientRect();
        const cs = getComputedStyle(article);
        const csBody = getComputedStyle(body);
        const csP = getComputedStyle(p);
        return {
            ok: true,
            articleAlignItems: cs.alignItems,
            articleDisplay: cs.display,
            bodyPaddingTop: csBody.paddingTop,
            pMarginTop: csP.marginTop,
            pMarginBottom: csP.marginBottom,
            bubbleCenterY: (br.top + br.bottom) / 2,
            bodyCenterY:   (bdr.top + bdr.bottom) / 2,
            pCenterY:      (pr.top + pr.bottom) / 2,
            bubbleHeight:  br.height,
            pHeight:       pr.height,
            bodyHeight:    bdr.height,
            pTop: pr.top,
            bubbleTop: br.top,
        };
    });
    console.log(JSON.stringify(m, null, 2));
    await browser.close();
})();
