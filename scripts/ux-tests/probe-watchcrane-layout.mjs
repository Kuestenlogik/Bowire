// Probe — WatchCrane server-stream layout in the standalone Tool (5080).
// Reproduces: "incoming messages push the detail pane out of view and the
// list has no scrollbar" (harbor-demo HarborService/WatchCrane, 250 ms ticks).
//
// Usage: node scripts/ux-tests/probe-watchcrane-layout.mjs [outfile.png]
// Requires: bowire.exe on :5080, Samples.Combined on :5101.

import { chromium } from '@playwright/test';

const out = process.argv[2] || 'watchcrane.png';
const browser = await chromium.launch({ headless: true, args: ['--ignore-certificate-errors'] });
const page = await browser.newPage({ ignoreHTTPSErrors: true, viewport: { width: 1500, height: 900 } });

await page.goto('http://localhost:5080/', { waitUntil: 'domcontentloaded' });
await page.evaluate(() => {
    // A fresh profile has no workspace record; the app falls back to the
    // 'orphan' scope, so that is the prefix the source list must use.
    localStorage.setItem('bowire_ws_orphan_server_urls', JSON.stringify(['https://localhost:5101']));
    localStorage.setItem('bowire_rail_mode', 'discover');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForSelector('#bowire-app', { timeout: 30000 });
// Discovery against :5101 has to land before the group exists.
await page.waitForFunction(
    () => [...document.querySelectorAll('.bowire-service-group')]
        .some(x => /HarborService/.test(x.textContent)),
    null, { timeout: 60000 });

// Expand the HarborService group — collapsed groups render no method rows.
await page.evaluate(() => {
    const g = [...document.querySelectorAll('.bowire-service-group')]
        .find(x => /HarborService/.test(x.textContent));
    (g?.querySelector('.bowire-service-header'))?.click();
});
await page.waitForSelector('.bowire-method-item:has-text("WatchCrane")', { timeout: 20000 });
await page.locator('.bowire-method-item', { hasText: 'WatchCrane' }).first().click();
await page.waitForTimeout(1200);

// craneId — seeded cranes are 1..3; 0 raises NotFound.
await page.locator('.bowire-form-input').first().fill('1');
await page.waitForTimeout(400);
await page.locator('#bowire-action-execute-btn').click();

await page.waitForFunction(
    () => document.querySelectorAll('.bowire-stream-list-item').length >= 25,
    null, { timeout: 40000 });
await page.waitForTimeout(600);

// --inject-css: apply the candidate fix at runtime, so the layout can be
// verified without rebuilding the Tool (bowire.css is an EmbeddedResource).
if (process.argv.includes('--inject-css')) {
    await page.addStyleTag({ content: `
        .bowire-streaming-with-placeholders {
            display: flex; flex-direction: column; height: 100%; min-height: 0;
        }
        .bowire-streaming-with-placeholders > .bowire-response-output.streaming {
            flex: 1 1 auto; min-height: 0;
        }
        .bowire-streaming-with-placeholders > .bowire-placeholder-slot {
            flex: 0 0 auto;
        }
    ` });
    await page.waitForTimeout(400);
}

const state = await page.evaluate(() => {
    const h = el => el ? Math.round(el.getBoundingClientRect().height) : null;
    const output = document.getElementById('bowire-stream-output');
    const wrapper = output?.parentElement;
    const paneBody = wrapper?.parentElement;
    const list = document.querySelector('.bowire-stream-list-pane');
    const detail = document.querySelector('.bowire-stream-detail-pane');
    const detailBody = document.querySelector('.bowire-stream-detail-body');
    const cs = el => el ? getComputedStyle(el) : {};
    return {
        frames: document.querySelectorAll('.bowire-stream-list-item').length,
        paneBodyH: h(paneBody),
        wrapperClass: wrapper?.className,
        wrapperH: h(wrapper),
        wrapperDisplay: cs(wrapper).display,
        wrapperMinH: cs(wrapper).minHeight,
        outputH: h(output),
        listH: h(list),
        listScrollH: list?.scrollHeight,
        listScrolls: list ? list.scrollHeight > list.clientHeight + 1 : null,
        listOverflowY: cs(list).overflowY,
        detailH: h(detail),
        detailBodyH: h(detailBody),
        outputFitsParent: h(output) <= h(paneBody) + 1,
    };
});
console.log(JSON.stringify(state, null, 2));
await page.screenshot({ path: out, fullPage: false });
console.log('screenshot -> ' + out);
await browser.close();
