// Compare sidebar-toolbar heights across rails — operator notes Discover's
// toolbar is shorter than others, suspects it's because it has no buttons.

import { chromium } from '@playwright/test';

const browser = await chromium.launch({ headless: true, args: ['--ignore-certificate-errors'] });
const page = await browser.newPage({ ignoreHTTPSErrors: true, viewport: { width: 1400, height: 900 } });

await page.goto('http://localhost:5180/', { waitUntil: 'domcontentloaded' });
await page.evaluate(() => {
    const id = 'probe';
    localStorage.setItem('bowire_workspaces', JSON.stringify([{
        id, name: 'Probe', color: 'sky', createdAt: 1_700_000_000_000,
    }]));
    localStorage.setItem('bowire_active_workspace_id', id);
    localStorage.setItem('bowire_ws_probe_server_urls', JSON.stringify(['http://localhost:9999/']));
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });

const measurements = {};
for (const railId of ['discover', 'compose', 'recordings', 'flows', 'mocks', 'workspaces']) {
    await page.evaluate((id) => { localStorage.setItem('bowire_rail_mode', id); }, railId);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 }).catch(() => {});
    await page.waitForTimeout(800);
    measurements[railId] = await page.evaluate(() => {
        const tb = document.querySelector('.bowire-sidebar .bowire-sidebar-toolbar');
        if (!tb) return null;
        const r = tb.getBoundingClientRect();
        const cs = getComputedStyle(tb);
        return {
            h: r.height,
            paddingTop: cs.paddingTop,
            paddingBottom: cs.paddingBottom,
            minHeight: cs.minHeight,
            children: tb.children.length,
            hasButton: !!tb.querySelector('button'),
        };
    });
}

console.log(JSON.stringify(measurements, null, 2));
await browser.close();
