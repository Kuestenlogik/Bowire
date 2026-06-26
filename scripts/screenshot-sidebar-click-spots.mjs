import { chromium } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

const outDir = resolve('artifacts/screenshots');
mkdirSync(outDir, { recursive: true });

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 800 } });

await page.goto('http://localhost:5180/');
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });

await page.evaluate(() => {
  const id = 'ws-demo';
  localStorage.setItem('bowire_workspaces', JSON.stringify([{ id, name: 'Demo', color: 'sky', createdAt: Date.now() }]));
  localStorage.setItem('bowire_active_workspace_id', id);
  localStorage.setItem('bowire_sidebar_collapsed', '1');
});
await page.reload();
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });

// Pick the discover rail (sidebar has actual content there)
const railBtn = await page.$('[data-rail-mode-id="discover"]');
if (railBtn) await railBtn.click();
await page.waitForTimeout(300);

// Click NEAR THE TOP of the splitter (far from chevron) — verify it expands
const splitter = await page.$('#bowire-sidebar-splitter');
const box = await splitter.boundingBox();

// Move cursor to TOP of the bar
await page.mouse.move(box.x + box.width / 2, box.y + 40);
await page.waitForTimeout(150);
await page.screenshot({ path: resolve(outDir, 'sidebar-collapsed-cursor-top.png'), fullPage: false });
console.log('cursor at top of splitter');

// Click here
await page.mouse.down();
await page.mouse.up();
await page.waitForTimeout(300);
await page.screenshot({ path: resolve(outDir, 'sidebar-after-top-click.png'), fullPage: false });
console.log('after click at top → should be expanded');

await browser.close();
