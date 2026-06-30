import { chromium } from '@playwright/test';
const browser = await chromium.launch({ headless: true, args: ['--ignore-certificate-errors'] });
const page = await browser.newPage({ ignoreHTTPSErrors: true, viewport: { width: 1400, height: 900 } });
await page.goto('https://localhost:5101/bowire', { waitUntil: 'domcontentloaded' });
await page.evaluate(() => {
    const id = 'harbor';
    localStorage.setItem('bowire_workspaces', JSON.stringify([{id,name:'Harbor demo',color:'sky',createdAt:1_700_000_000_000}]));
    localStorage.setItem('bowire_active_workspace_id', id);
    localStorage.setItem('bowire_ws_harbor_server_urls', JSON.stringify(['https://localhost:5101/']));
    localStorage.setItem('bowire_rail_mode', 'discover');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 30000 });
const groups = await page.locator('.bowire-service-group').all();
for (const g of groups) {
    const c = await g.locator('.bowire-service-chevron').first().getAttribute('class').catch(()=>'');
    if (!c || !c.includes('expanded')) await g.locator('.bowire-service-header').first().click().catch(()=>{});
}
await page.waitForTimeout(400);
await page.locator('.bowire-method-item', { hasText: 'WatchCrane' }).first().click();
await page.waitForTimeout(800);
await page.locator('input[data-field-key="craneId"]').first().fill('1').catch(()=>{});
await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
await page.waitForFunction(() => document.querySelectorAll('.bowire-stream-list-item').length >= 8, null, { timeout: 30000 });
await page.waitForTimeout(500);

const inspect = await page.evaluate(() => {
    const ids = ['bowire-stream-output'];
    const sels = ['.bowire-pane-body', '.bowire-stream-list-pane', '.bowire-stream-splitter', '.bowire-stream-detail-pane', '.bowire-stream-toolbar'];
    const all = ['bowire-pane-body','bowire-stream-output','bowire-stream-list-pane','bowire-stream-splitter','bowire-stream-detail-pane','bowire-stream-toolbar'];
    const rect = el => el ? { x: el.getBoundingClientRect().x, y: el.getBoundingClientRect().y, w: el.getBoundingClientRect().width, h: el.getBoundingClientRect().height } : null;
    const getEl = sel => sel.startsWith('#') ? document.getElementById(sel.slice(1)) : document.querySelector(sel);
    const data = {};
    [...ids.map(i=>'#'+i), ...sels].forEach(sel => {
        const el = getEl(sel);
        if (!el) { data[sel] = null; return; }
        const cs = getComputedStyle(el);
        data[sel] = {
            rect: rect(el),
            display: cs.display,
            flexDirection: cs.flexDirection,
            flex: cs.flex,
            height: cs.height,
            minHeight: cs.minHeight,
            maxHeight: cs.maxHeight,
            overflow: cs.overflow,
            parentTag: el.parentElement?.tagName + '.' + el.parentElement?.className,
        };
    });
    return data;
});
console.log(JSON.stringify(inspect, null, 2));
await browser.close();
