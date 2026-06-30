// Probe: when many stream messages arrive, does the detail pane get
// crushed? Operator: 'bei mehreren messages als antwort (subscribe)
// scheint es so zu sein, dass die liste der nachrichten dann die
// anzeige des inhalts einer message (JSON etc.) verdrängt.'
//
// Drives the Combined Harbor sample's WatchCrane stream, waits for ~8
// messages, then logs the computed heights of the list pane + detail
// pane + .bowire-stream-detail-body to see if anything is mis-sized.

import { chromium } from '@playwright/test';

const browser = await chromium.launch({
    headless: true,
    args: ['--ignore-certificate-errors'],
});
const page = await browser.newPage({
    ignoreHTTPSErrors: true,
    viewport: { width: 1400, height: 900 },
});

await page.goto('https://localhost:5101/bowire', { waitUntil: 'domcontentloaded' });
await page.evaluate(() => {
    const id = 'harbor';
    localStorage.setItem('bowire_workspaces', JSON.stringify([{
        id, name: 'Harbor demo', color: 'sky', createdAt: 1_700_000_000_000,
    }]));
    localStorage.setItem('bowire_active_workspace_id', id);
    localStorage.setItem('bowire_ws_harbor_server_urls', JSON.stringify(['https://localhost:5101/']));
    localStorage.setItem('bowire_rail_mode', 'discover');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 30000 });
// Expand any collapsed service groups so the method items are visible/clickable.
const groups = await page.locator('.bowire-service-group').all();
for (const group of groups) {
    const chev = group.locator('.bowire-service-chevron').first();
    const cls = await chev.getAttribute('class').catch(() => '');
    if (!cls || !cls.includes('expanded')) {
        await group.locator('.bowire-service-header').first().click().catch(() => {});
        await page.waitForTimeout(80);
    }
}
await page.waitForTimeout(400);

const stream = page.locator('.bowire-method-item', { hasText: 'WatchCrane' }).first();
await stream.scrollIntoViewIfNeeded();
await stream.click();
await page.waitForTimeout(800);

const craneId = page.locator('input[data-field-key="craneId"]').first();
if (await craneId.isVisible().catch(() => false)) {
    await craneId.fill('1');
}
const exec = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
await exec.click();

// Wait until ~8 stream messages have arrived
await page.waitForFunction(() => {
    return document.querySelectorAll('.bowire-stream-list-item').length >= 8;
}, null, { timeout: 30000 });

await page.waitForTimeout(500);

const state = await page.evaluate(() => {
    const output = document.getElementById('bowire-stream-output');
    const list = document.querySelector('.bowire-stream-list-pane');
    const detail = document.querySelector('.bowire-stream-detail-pane');
    const detailBody = document.querySelector('.bowire-stream-detail-body');
    const items = document.querySelectorAll('.bowire-stream-list-item');
    if (!output) return { error: 'no output' };
    const rect = (el) => el ? el.getBoundingClientRect() : null;
    return {
        outputH: output.getBoundingClientRect().height,
        listH: rect(list)?.height,
        detailH: rect(detail)?.height,
        detailBodyH: rect(detailBody)?.height,
        listPct: getComputedStyle(output).getPropertyValue('--bowire-stream-list-pct').trim(),
        listItemCount: items.length,
        listScrollH: list?.scrollHeight,
        detailBodyText: detailBody?.textContent?.substring(0, 120),
    };
});

console.log(JSON.stringify(state, null, 2));

await page.screenshot({ path: 'scripts/ux-tests/probe-stream-detail-shrink.png', fullPage: false });

await browser.close();
