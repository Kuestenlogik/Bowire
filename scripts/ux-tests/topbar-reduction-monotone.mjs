// Smoke test for the topbar's per-item responsive overflow ladder.
//
// Drags the viewport from wide to narrow in fixed steps and asserts
// monotonicity: as the width shrinks, the count of visible right-
// cluster buttons must never INCREASE. A regression (the bug the
// commit before this script fixed) shows up as a width step where
// items pop back out — exactly the "pop in, pop back out, then per-
// item" thrash the operator reported.
//
// Run:  node scripts/ux-tests/topbar-reduction-monotone.mjs
//
// Requires the standalone Bowire Tool to be running on
// http://localhost:5180. If the tool isn't running, the script
// prints a hint and exits cleanly with code 1.

import { chromium } from '@playwright/test';

const BASE = process.env.BOWIRE_URL || 'http://localhost:5180';
const START_WIDTH = 1600;
const END_WIDTH = 320;
const STEP = 40;
const HEIGHT = 900;

// Wait for the layout pass to settle after a viewport resize. The
// ResizeObserver fires asynchronously and the layout pass itself
// reads through requestAnimationFrame from the bundle, so two raf
// ticks plus a tiny breath is enough headroom even on a busy CI box.
async function settle(page) {
    await page.evaluate(() => new Promise(r =>
        requestAnimationFrame(() => requestAnimationFrame(r))));
    await page.waitForTimeout(60);
}

// Counts the right-cluster's visible vs hidden tagged buttons. A
// "visible" button is a [data-topbar-priority] node whose offsetParent
// is not null AND whose computed display isn't none. The overflow
// button itself isn't tagged with a priority so it's correctly
// excluded.
async function measure(page) {
    return await page.evaluate(() => {
        const right = document.getElementById('bowire-topbar-right');
        if (!right) return { visible: -1, hidden: -1, overflowShown: false };
        const tagged = right.querySelectorAll('[data-topbar-priority]');
        let visible = 0, hidden = 0;
        tagged.forEach(n => {
            const style = getComputedStyle(n);
            const isVisible = n.offsetParent !== null && style.display !== 'none';
            if (isVisible) visible++; else hidden++;
        });
        const ovBtn = document.getElementById('bowire-topbar-right-overflow-btn');
        const ovGroup = right.querySelector('.bowire-topbar-right-overflow-group');
        const overflowShown = !!(ovGroup && getComputedStyle(ovGroup).display !== 'none');
        return {
            visible, hidden, overflowShown,
            ovHasClass: ovBtn ? ovBtn.classList.contains('has-overflow') : false,
        };
    });
}

let browser;
try {
    browser = await chromium.launch({ headless: true });
} catch (e) {
    console.error('[topbar-reduction-monotone] Could not launch Chromium:', e.message);
    process.exit(1);
}

const page = await browser.newPage({ viewport: { width: START_WIDTH, height: HEIGHT } });

try {
    await page.goto(BASE, { timeout: 8000 });
} catch (e) {
    console.error(`[topbar-reduction-monotone] ${BASE} not reachable — start the Tool first ` +
        `(dotnet run --project src/Kuestenlogik.Bowire.Tool).`);
    await browser.close();
    process.exit(1);
}
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });

// Seed a workspace so the workspace chip renders with a real label
// (= realistic width). Without this the chip reads "No workspace"
// which is narrower than typical and skews the test.
await page.evaluate(() => {
    const id = 'ws-smoke';
    localStorage.setItem('bowire_workspaces', JSON.stringify([
        { id, name: 'Smoke Test Workspace', color: 'sky', createdAt: Date.now() }
    ]));
    localStorage.setItem('bowire_active_workspace_id', id);
});
await page.reload();
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });

const widths = [];
for (let w = START_WIDTH; w >= END_WIDTH; w -= STEP) widths.push(w);

const samples = [];
let prevVisible = Infinity;
let regressions = [];

for (const w of widths) {
    await page.setViewportSize({ width: w, height: HEIGHT });
    await settle(page);
    const m = await measure(page);
    samples.push({ width: w, ...m });
    if (m.visible > prevVisible) {
        regressions.push({
            width: w,
            visible: m.visible,
            prevWidth: samples[samples.length - 2].width,
            prevVisible
        });
    }
    prevVisible = m.visible;
}

await browser.close();

// Report.
console.log('width  visible  hidden  overflow');
for (const s of samples) {
    console.log(
        `${String(s.width).padStart(5)}  ${String(s.visible).padStart(7)}  ` +
        `${String(s.hidden).padStart(6)}  ${s.overflowShown ? 'yes' : 'no'}`
    );
}

if (regressions.length > 0) {
    console.error('\n[FAIL] Visible-count INCREASED while width was DECREASING — the topbar ' +
        'overflow ladder is not monotone:');
    for (const r of regressions) {
        console.error(`  ${r.prevWidth}px (${r.prevVisible} visible) → ${r.width}px ` +
            `(${r.visible} visible)`);
    }
    process.exit(1);
}

console.log('\n[OK] Visible-count is monotone non-increasing across ' +
    `${widths.length} width steps (${START_WIDTH}→${END_WIDTH}, step ${STEP}px).`);
