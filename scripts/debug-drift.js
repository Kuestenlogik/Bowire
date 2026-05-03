const { chromium } = require('@playwright/test');
(async () => {
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    const page = await ctx.newPage();
    page.on('console', msg => console.log('[BROWSER]', msg.type(), msg.text()));
    await page.goto('http://localhost:4000/', { waitUntil: 'networkidle' });
    await page.waitForTimeout(500);
    await page.locator('.screenshot-carousel').scrollIntoViewIfNeeded();
    await page.waitForTimeout(300);

    // Move mouse off the carousel so hover doesn't pause it
    await page.mouse.move(100, 100);

    // Inspect carousel state
    const info = await page.evaluate(() => {
        const c = document.querySelector('.screenshot-carousel');
        const t = document.querySelector('.screenshot-carousel-track');
        return {
            carouselClientWidth: c?.clientWidth,
            carouselScrollWidth: c?.scrollWidth,
            trackScrollWidth: t?.scrollWidth,
            carouselScrollLeft: c?.scrollLeft,
            isHovered: c?.matches(':hover'),
            isFocused: c?.matches(':focus-within'),
            hasActive: c?.matches(':active'),
            paddingLeft: t?.style.paddingLeft,
            paddingRight: t?.style.paddingRight,
            firstCardWidth: document.querySelector('.screenshot-card')?.offsetWidth,
            totalChildren: t?.children.length,
            child13OffsetLeft: t?.children[13]?.offsetLeft,
            child26OffsetLeft: t?.children[26]?.offsetLeft,
            child13IsClone: t?.children[13]?.classList.contains('is-clone'),
            child26IsClone: t?.children[26]?.classList.contains('is-clone')
        };
    });
    console.log('Carousel state:', info);

    const before = await page.evaluate(() => document.querySelector('.screenshot-carousel').scrollLeft);
    console.log('scrollLeft before 5s wait:', before);
    await page.waitForTimeout(5000);
    const after = await page.evaluate(() => document.querySelector('.screenshot-carousel').scrollLeft);
    console.log('scrollLeft after 5s wait:', after);
    console.log('Moved:', after - before, 'px');
    await browser.close();
})();
