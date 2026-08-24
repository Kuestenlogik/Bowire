// Probe — response action cluster (copy / download / expand / collapse)
// after the icon-only pass. Uses a UNARY method so the JSON-tree
// expand/collapse pair is rendered (it is suppressed while streaming).
//
// Usage: node scripts/ux-tests/probe-response-actions.mjs [outfile.png]
// Requires: bowire.exe on :5080, Samples.Combined on :5101.

import { chromium } from '@playwright/test';

const out = process.argv[2] || 'response-actions.png';
const browser = await chromium.launch({ headless: true, args: ['--ignore-certificate-errors'] });
const page = await browser.newPage({ ignoreHTTPSErrors: true, viewport: { width: 1500, height: 900 } });

await page.goto('http://localhost:5080/', { waitUntil: 'domcontentloaded' });
await page.evaluate(() => {
    localStorage.setItem('bowire_ws_orphan_server_urls', JSON.stringify(['https://localhost:5101']));
    localStorage.setItem('bowire_rail_mode', 'discover');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForSelector('#bowire-app', { timeout: 30000 });
await page.waitForFunction(
    () => [...document.querySelectorAll('.bowire-service-group')].some(x => /SHIPS/i.test(x.textContent)),
    null, { timeout: 60000 });

await page.evaluate(() => {
    const g = [...document.querySelectorAll('.bowire-service-group')].find(x => /SHIPS/i.test(x.textContent));
    g?.querySelector('.bowire-service-header')?.click();
});
await page.waitForTimeout(1000);
await page.locator(String.raw`.bowire-method-item`).first().click();
await page.waitForTimeout(1000);
await page.locator('#bowire-action-execute-btn').click();
await page.waitForTimeout(4000);

const state = await page.evaluate(() => {
    const pick = id => {
        const b = document.getElementById(id);
        if (!b) return null;
        const r = b.getBoundingClientRect();
        return {
            w: Math.round(r.width), h: Math.round(r.height),
            title: b.title, aria: b.getAttribute('aria-label'),
            text: b.textContent.trim(), hasSvg: !!b.querySelector('svg'),
        };
    };
    const row = document.querySelector('.bowire-pane-actions');
    return {
        copy: pick('bowire-response-copy-main-btn'),
        caret: pick('bowire-response-copy-caret-btn'),
        download: pick('bowire-response-download-btn'),
        expand: pick('bowire-response-tree-expand-btn'),
        collapse: pick('bowire-response-tree-collapse-btn'),
        clusterWidth: row ? Math.round(row.getBoundingClientRect().width) : null,
        method: document.querySelector('.bowire-method-item.active,.bowire-method-item.selected')?.textContent.trim().slice(0,40),
        responseText: document.querySelector('.bowire-response-output')?.textContent.trim().slice(0, 120),
        treeNodes: document.querySelectorAll('.bowire-json-tree-node').length,
        paneBtns: [...document.querySelectorAll('.bowire-pane-actions button')].map(b => b.id || b.textContent.trim().slice(0,18)),
    };
});
console.log(JSON.stringify(state, null, 2));

await page.screenshot({ path: out });
console.log('screenshot -> ' + out);
await browser.close();
