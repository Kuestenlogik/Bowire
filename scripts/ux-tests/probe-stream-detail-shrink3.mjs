// Probe v3 — targets the TOOL (5180) with TacticalApi (5182) gRPC stream.
// Tool has the fresh CSS (post-fix); Combined sample at 5101 has its own
// Bowire copy from the sibling repo, so doesn't reflect main-repo CSS changes.

import { chromium } from '@playwright/test';

const browser = await chromium.launch({ headless: true, args: ['--ignore-certificate-errors'] });
const page = await browser.newPage({ ignoreHTTPSErrors: true, viewport: { width: 1400, height: 900 } });

await page.goto('http://localhost:5181/bowire', { waitUntil: 'domcontentloaded' });
await page.evaluate(() => {
    const id = 'embedded';
    localStorage.setItem('bowire_workspaces', JSON.stringify([{
        id, name: 'Embedded probe', color: 'sky', createdAt: 1_700_000_000_000,
    }]));
    localStorage.setItem('bowire_active_workspace_id', id);
    localStorage.setItem('bowire_ws_embedded_server_urls', JSON.stringify(['http://localhost:5181/']));
    localStorage.setItem('bowire_rail_mode', 'discover');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 45000 });

const groups = await page.locator('.bowire-service-group').all();
for (const g of groups) {
    const c = await g.locator('.bowire-service-chevron').first().getAttribute('class').catch(()=>'');
    if (!c || !c.includes('expanded')) await g.locator('.bowire-service-header').first().click().catch(()=>{});
}
await page.waitForTimeout(400);

// SituationService.SubscribeSituationObjectEvents — server-streaming
const stream = page.locator('.bowire-method-item', { hasText: /location|stream|sse/i }).first();
await stream.scrollIntoViewIfNeeded();
await stream.click();
await page.waitForTimeout(800);

const exec = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
await exec.click();
await page.waitForFunction(() => document.querySelectorAll('.bowire-stream-list-item').length >= 8, null, { timeout: 30000 });
await page.waitForTimeout(500);

const state = await page.evaluate(() => {
    const output = document.getElementById('bowire-stream-output');
    const paneBody = output?.parentElement;
    const list = document.querySelector('.bowire-stream-list-pane');
    const detail = document.querySelector('.bowire-stream-detail-pane');
    const detailBody = document.querySelector('.bowire-stream-detail-body');
    const items = document.querySelectorAll('.bowire-stream-list-item');
    const h = el => el?.getBoundingClientRect().height;
    return {
        paneBodyH: h(paneBody),
        outputH: h(output),
        listH: h(list),
        detailH: h(detail),
        detailBodyH: h(detailBody),
        listItemCount: items.length,
        ratio: { list: (h(list) / h(output) * 100).toFixed(1) + '%', detail: (h(detail) / h(output) * 100).toFixed(1) + '%' },
        listScrollH: list?.scrollHeight,
        overflowOK: h(output) <= h(paneBody) + 1,
        outputMaxHeight: getComputedStyle(output).maxHeight,
    };
});
console.log(JSON.stringify(state, null, 2));
await browser.close();
