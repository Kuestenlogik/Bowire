const { chromium } = require('@playwright/test');
(async () => {
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({
        viewport: { width: 1280, height: 720 },
        ignoreHTTPSErrors: true,
        colorScheme: 'dark'
    });
    const page = await ctx.newPage();
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.goto('http://localhost:5180/', { waitUntil: 'domcontentloaded' });
    await page.evaluate(() => {
        try { localStorage.clear(); sessionStorage.clear(); } catch {}
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
    await page.waitForTimeout(1500);
    const info = await page.evaluate(() => {
        const findSel = (s) => !!document.querySelector(s);
        const text = (document.body && document.body.innerText) || '';
        return {
            railMode: localStorage.getItem('bowire_rail_mode'),
            workspaces: localStorage.getItem('bowire_workspaces'),
            hasHomeFirstrun: findSel('.bowire-home-band-firstrun'),
            hasHomeWrap: findSel('.bowire-home-wrap'),
            hasMainHome: findSel('#bowire-main-home'),
            hasLandingHero: findSel('.bowire-landing-hero'),
            hasStartGrid: findSel('.bowire-home-start-grid'),
            hasEmptyCard: findSel('.bowire-empty-card'),
            hasLandingCard: findSel('.bowire-landing-card'),
            textSnippet: text.substring(0, 600)
        };
    });
    console.log(JSON.stringify(info, null, 2));
    await page.screenshot({ path: 'C:/Projekte/Kuestenlogik/Bowire/scripts/probe-firstrun.png', fullPage: false });
    await browser.close();
})();
