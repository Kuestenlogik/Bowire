// Probe: identify the "two horizontal lines" above 'No services discovered yet.'
// in the Discover sidebar empty state. Operator reports them as visually unclear.

import { chromium } from '@playwright/test';

const browser = await chromium.launch({ headless: true, args: ['--ignore-certificate-errors'] });
const page = await browser.newPage({ ignoreHTTPSErrors: true, viewport: { width: 1400, height: 900 } });

await page.goto('http://localhost:5180/', { waitUntil: 'domcontentloaded' });
// Seed a workspace + a URL that won't discover anything (unreachable).
// This lands in the "source panel rendered, services list empty" state
// that the operator sees the two lines above 'No services discovered yet'.
await page.evaluate(() => {
    const id = 'probe';
    localStorage.setItem('bowire_workspaces', JSON.stringify([{
        id, name: 'Empty probe', color: 'sky', createdAt: 1_700_000_000_000,
    }]));
    localStorage.setItem('bowire_active_workspace_id', id);
    localStorage.setItem('bowire_ws_probe_server_urls', JSON.stringify(['http://localhost:9999/']));
    localStorage.setItem('bowire_rail_mode', 'discover');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
await page.waitForTimeout(3500);

const inspect = await page.evaluate(() => {
    // Also collect the sidebar header + toolbar info for the height-diff comparison
    const sidebarHeader = document.querySelector('.bowire-sidebar-header, .bowire-sidebar-title, .bowire-sidebar h2, .bowire-sidebar h3');
    const sidebarToolbar = document.querySelector('.bowire-sidebar .bowire-sidebar-toolbar, .bowire-sidebar-toolbar');
    const allLines = [];
    document.querySelectorAll('.bowire-sidebar *').forEach(el => {
        const cs = getComputedStyle(el);
        const bt = parseFloat(cs.borderTopWidth);
        const bb = parseFloat(cs.borderBottomWidth);
        if (bt > 0 || bb > 0) {
            const r = el.getBoundingClientRect();
            allLines.push({
                tag: el.tagName, class: el.className.substring(0, 60),
                y: r.y, h: r.height,
                bt: cs.borderTopWidth + ' ' + cs.borderTopStyle + ' ' + cs.borderTopColor,
                bb: cs.borderBottomWidth + ' ' + cs.borderBottomStyle + ' ' + cs.borderBottomColor,
                text: (el.textContent || '').substring(0, 40),
            });
        }
    });
    const empty = [...document.querySelectorAll('.bowire-pane-empty')].find(
        el => el.textContent.includes('No services discovered yet')
    );
    if (!empty) return { error: 'no empty state visible' };

    // Walk siblings above the empty element to enumerate what could draw lines.
    const result = { empty: { tag: empty.tagName, class: empty.className } };
    const siblings = [];
    let n = empty.previousElementSibling;
    while (n && siblings.length < 8) {
        const cs = getComputedStyle(n);
        siblings.push({
            tag: n.tagName,
            class: n.className,
            id: n.id,
            display: cs.display,
            borderTop: cs.borderTopWidth + ' ' + cs.borderTopStyle + ' ' + cs.borderTopColor,
            borderBottom: cs.borderBottomWidth + ' ' + cs.borderBottomStyle + ' ' + cs.borderBottomColor,
            marginTop: cs.marginTop, marginBottom: cs.marginBottom,
            rect: { y: n.getBoundingClientRect().y, h: n.getBoundingClientRect().height },
            text: (n.textContent || '').substring(0, 60),
        });
        n = n.previousElementSibling;
    }
    result.siblingsAbove = siblings;

    // Walk up the parent chain to see ancestors with borders.
    const ancestors = [];
    let p = empty.parentElement;
    while (p && ancestors.length < 6 && p.id !== 'bowire-app') {
        const cs = getComputedStyle(p);
        ancestors.push({
            tag: p.tagName,
            class: p.className,
            borderTop: cs.borderTopWidth + ' ' + cs.borderTopStyle + ' ' + cs.borderTopColor,
            borderBottom: cs.borderBottomWidth + ' ' + cs.borderBottomStyle + ' ' + cs.borderBottomColor,
            paddingTop: cs.paddingTop,
        });
        p = p.parentElement;
    }
    result.ancestors = ancestors;

    result.borderedElsAboveEmpty = allLines.filter(l => l.y < empty.getBoundingClientRect().y).sort((a, b) => a.y - b.y);
    return result;
});

console.log(JSON.stringify(inspect, null, 2));

await page.screenshot({ path: 'scripts/ux-tests/probe-discover-empty-lines.png', clip: { x: 0, y: 0, width: 600, height: 600 } });

await browser.close();
