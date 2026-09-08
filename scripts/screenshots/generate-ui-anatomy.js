/**
 * Generates docs/images/ui-anatomy-{light,dark}.svg — the clickable layout
 * diagram on docs/ui-guide/index.md.
 *
 * This used to be a hand-drawn SVG, and it rotted: it labelled the sidebar's
 * regions "Favourites / Environments / Flows", which were tabs inside the
 * sidebar before the rail strip replaced them, and it carried no rail strip
 * at all. A diagram nobody can regenerate is a diagram that describes
 * whichever UI existed when somebody last had the patience to redraw it.
 *
 * So the regions are not drawn from memory: the script opens the workbench,
 * asks the DOM for each region's bounding box, and lays the callouts on
 * those coordinates. A region whose selector no longer matches is reported
 * and skipped rather than drawn in the wrong place.
 *
 * Output: docs/images/ui-anatomy-{light,dark}.svg
 *
 * Operator MUST already have this running (operator-managed):
 *   https://localhost:5101/bowire   Combined Harbor sample
 *
 * Usage: node scripts/screenshots/generate-ui-anatomy.js
 */

const path = require('path');
const fs = require('fs');
// Canonical sidebar helpers — the same ones capture-ui-guide.js uses.
const sidebar = require('../lib/sidebar.cjs');

let chromium;
try { chromium = require('@playwright/test').chromium; }
catch (e) {
    console.error('[bail] @playwright/test not available:', e.message);
    process.exit(2);
}

const OUT = path.resolve(__dirname, '..', '..', 'docs', 'images');
const TARGET = 'https://localhost:5101/bowire';
const WIDTH = 1400;
const HEIGHT = 900;

// Each region carries the page it links to, so the diagram stays a
// navigation surface rather than a picture. `side` places the callout
// label outside the region where there is room for it.
const REGIONS = [
    { key: 'topbar',       n: 1, label: 'Topbar',        href: 'topbar.html',        side: 'below',
      selectors: ['.bowire-topbar', 'header.bowire-header', '#bowire-topbar', '.bowire-header'] },
    { key: 'railStrip',    n: 2, label: 'Rail strip',    href: 'rail-strip.html',    side: 'right',
      selectors: ['#bowire-activity-rail', '.bowire-activity-rail', '.bowire-rail'] },
    { key: 'sidebar',      n: 3, label: 'Sidebar',       href: 'sidebar.html',       side: 'inside',
      selectors: ['#bowire-sidebar', '.bowire-sidebar', 'aside.bowire-sidebar'] },
    { key: 'requestPane',  n: 4, label: 'Request pane',  href: 'request-pane.html',  side: 'inside',
      // The pane ids carry the selected method — #bowire-request-pane-Default-GetPort-calls —
      // so only a prefix match is stable across whatever the catalogue offers.
      selectors: ['[id^="bowire-request-pane-"]', '#bowire-request-pane'] },
    { key: 'responsePane', n: 5, label: 'Response pane', href: 'response-pane.html', side: 'inside',
      selectors: ['[id^="bowire-response-pane-"]', '#bowire-response-pane'] },
    { key: 'actionBar',    n: 6, label: 'Action bar',    href: 'action-bar.html',    side: 'above',
      selectors: ['#bowire-action-bar', '.bowire-action-bar', '.bowire-actions'] },
];

const THEMES = {
    light: { stroke: '#4f46e5', fill: 'rgba(79,70,229,0.08)', text: '#1e1b4b', badgeText: '#ffffff' },
    dark:  { stroke: '#a5b4fc', fill: 'rgba(165,180,252,0.12)', text: '#e0e7ff', badgeText: '#1e1b4b' },
};

function log(m) { console.log(new Date().toISOString().slice(11, 19), m); }

async function seed(page, theme) {
    await page.evaluate((theme) => {
        const id = 'harbor';
        try {
            localStorage.setItem('bowire_workspaces', JSON.stringify([{
                id, name: 'Harbor demo', color: 'sky', createdAt: 1_700_000_000_000,
            }]));
            localStorage.setItem('bowire_active_workspace_id', id);
            localStorage.setItem('bowire_ws_' + id + '_server_urls',
                JSON.stringify(['https://localhost:5101/bowire']));
            localStorage.setItem('bowire_rail_mode', 'discover');
            localStorage.setItem('bowire_theme_pref', theme);
        } catch { /* private mode — the shot still renders, just unseeded */ }
    }, theme);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });

    // Select a method before measuring. On the Discover landing screen the
    // request pane, response pane and action bar do not exist yet, so a
    // diagram built there can only ever label three of the six regions —
    // which is how the previous one came to describe a workbench nobody
    // sees once they start working.
    try {
        await sidebar.waitForCatalogue(page, { timeout: 30000 });
        await page.waitForTimeout(600);
        await sidebar.openCatalogue(page, { timeout: 30000 });
        await page.locator('.bowire-method-item').first().click();
        await page.waitForTimeout(1200);
    } catch (e) {
        console.error('  [warn] could not select a method:', e.message);
    }
    await page.waitForTimeout(1500);
}

/** Pick the first selector that resolves to a laid-out element. */
async function measure(page) {
    return page.evaluate((regions) => {
        const out = {};
        for (const r of regions) {
            out[r.key] = null;
            for (const sel of r.selectors) {
                const el = document.querySelector(sel);
                if (el && (el.offsetWidth || el.offsetHeight)) {
                    const b = el.getBoundingClientRect();
                    out[r.key] = {
                        sel,
                        x: Math.round(b.x), y: Math.round(b.y),
                        w: Math.round(b.width), h: Math.round(b.height),
                    };
                    break;
                }
            }
        }
        return out;
    }, REGIONS.map((r) => ({ key: r.key, selectors: r.selectors })));
}

function callout(region, box, palette) {
    // Keep the label inside the canvas whichever side it is placed on.
    let lx = box.x + 12;
    let ly = box.y + 26;
    if (region.side === 'below') { ly = box.y + box.h + 26; }
    if (region.side === 'above') { ly = Math.max(20, box.y - 12); }
    if (region.side === 'right') { lx = box.x + box.w + 14; ly = box.y + 32; }
    lx = Math.min(lx, WIDTH - 150);
    ly = Math.min(Math.max(ly, 20), HEIGHT - 12);

    return `  <a href="${region.href}" aria-label="${region.label}">
    <rect x="${box.x + 1}" y="${box.y + 1}" width="${Math.max(box.w - 2, 2)}" height="${Math.max(box.h - 2, 2)}"
          rx="6" fill="${palette.fill}" stroke="${palette.stroke}" stroke-width="2"/>
    <circle cx="${lx + 11}" cy="${ly - 5}" r="11" fill="${palette.stroke}"/>
    <text x="${lx + 11}" y="${ly - 1}" font-family="system-ui,-apple-system,Segoe UI,sans-serif"
          font-size="12" font-weight="700" fill="${palette.badgeText}" text-anchor="middle">${region.n}</text>
    <text x="${lx + 29}" y="${ly}" font-family="system-ui,-apple-system,Segoe UI,sans-serif"
          font-size="13" font-weight="600" fill="${palette.text}">${region.label}</text>
  </a>`;
}

function buildSvg(pngBase64, boxes, palette) {
    const shapes = REGIONS
        .filter((r) => boxes[r.key])
        .map((r) => callout(r, boxes[r.key], palette))
        .join('\n');
    return `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"
     viewBox="0 0 ${WIDTH} ${HEIGHT}" width="${WIDTH}" height="${HEIGHT}" role="img"
     aria-label="Bowire workbench layout — click any region to open its UI Guide page">
  <title>Bowire workbench layout</title>
  <image href="data:image/png;base64,${pngBase64}" x="0" y="0" width="${WIDTH}" height="${HEIGHT}"/>
${shapes}
</svg>
`;
}

(async () => {
    fs.mkdirSync(OUT, { recursive: true });
    const browser = await chromium.launch({
        args: ['--ignore-certificate-errors', '--use-gl=swiftshader', '--enable-unsafe-swiftshader'],
    });
    let wrote = 0;

    for (const theme of ['light', 'dark']) {
        const page = await browser.newPage({
            viewport: { width: WIDTH, height: HEIGHT },
            deviceScaleFactor: 1,
            ignoreHTTPSErrors: true,
            colorScheme: theme,
        });
        try {
            await page.goto(TARGET, { waitUntil: 'domcontentloaded' });
            await seed(page, theme);

            const boxes = await measure(page);
            for (const r of REGIONS) {
                const b = boxes[r.key];
                log(`  ${theme} ${r.label.padEnd(14)} ${b ? `${b.sel.padEnd(24)} ${b.w}x${b.h} @ ${b.x},${b.y}` : '** selector matched nothing — skipped **'}`);
            }

            const png = await page.screenshot({ type: 'png' });
            const svg = buildSvg(png.toString('base64'), boxes, THEMES[theme]);
            const dest = path.join(OUT, `ui-anatomy-${theme}.svg`);
            fs.writeFileSync(dest, svg, 'utf8');
            log(`${theme}: wrote ${path.relative(process.cwd(), dest)} (${Math.round(svg.length / 1024)} KB)`);
            wrote++;
        } catch (e) {
            console.error(`[${theme}] failed:`, e.message);
        } finally {
            await page.close();
        }
    }

    await browser.close();
    process.exit(wrote > 0 ? 0 : 1);
})();
