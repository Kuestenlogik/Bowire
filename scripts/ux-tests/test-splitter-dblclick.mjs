import { chromium } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

const outDir = resolve('artifacts/screenshots');
mkdirSync(outDir, { recursive: true });

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 800 } });
page.on('console', msg => console.log('[browser]', msg.type(), msg.text()));

await page.goto('http://localhost:5180/');
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });
await page.evaluate(() => {
  const id = 'ws-demo';
  localStorage.setItem('bowire_workspaces', JSON.stringify([{ id, name: 'Demo', color: 'sky', createdAt: Date.now() }]));
  localStorage.setItem('bowire_active_workspace_id', id);
  localStorage.removeItem('bowire_sidebar_collapsed'); // ensure expanded
});
await page.reload();
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });
await page.click('[data-rail-mode-id="discover"]');
await page.waitForTimeout(300);

console.log('--- state pre-dblclick:');
const stateBefore = await page.evaluate(() => ({
  appBodyClass: document.getElementById('bowire-app-body')?.className,
  hasSplitter: !!document.getElementById('bowire-sidebar-splitter'),
  splitterRect: document.getElementById('bowire-sidebar-splitter')?.getBoundingClientRect().toJSON(),
}));
console.log(JSON.stringify(stateBefore, null, 2));

await page.screenshot({ path: resolve(outDir, 'dblclick-pre.png') });

// Double-click in middle of bar (NOT on the chevron — way up at top of bar)
const sp = await page.$('#bowire-sidebar-splitter');
const box = await sp.boundingBox();
await page.mouse.dblclick(box.x + box.width / 2, box.y + 60);
await page.waitForTimeout(400);

console.log('--- state after dblclick:');
const stateAfter = await page.evaluate(() => ({
  appBodyClass: document.getElementById('bowire-app-body')?.className,
  sidebarCollapsed: localStorage.getItem('bowire_sidebar_collapsed'),
}));
console.log(JSON.stringify(stateAfter, null, 2));

await page.screenshot({ path: resolve(outDir, 'dblclick-post.png') });

await browser.close();
