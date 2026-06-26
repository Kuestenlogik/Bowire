import { chromium } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

const outDir = resolve('artifacts/screenshots');
mkdirSync(outDir, { recursive: true });

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 800 } });

await page.goto('http://localhost:5180/');
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });
await page.waitForTimeout(400);

// Create a workspace so the sidebar has content
await page.evaluate(() => {
  const wsKey = 'bowire_workspaces';
  const activeKey = 'bowire_active_workspace_id';
  const id = 'ws-demo';
  localStorage.setItem(wsKey, JSON.stringify([{ id, name: 'Demo', color: 'sky', createdAt: Date.now() }]));
  localStorage.setItem(activeKey, id);
});
await page.reload();
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });

// Switch to discover rail so sidebar shows services
const railBtn = await page.$('[data-rail-mode-id="discover"]');
if (railBtn) await railBtn.click();
await page.waitForTimeout(300);

// EXPANDED screenshot
await page.screenshot({ path: resolve(outDir, 'sidebar-expanded.png'), fullPage: false });
console.log('expanded screenshot saved');

// Now collapse via Ctrl+B
await page.keyboard.press('Control+B');
await page.waitForTimeout(300);

await page.screenshot({ path: resolve(outDir, 'sidebar-collapsed.png'), fullPage: false });
console.log('collapsed screenshot saved');

// Hover over the collapsed splitter to show the hover state
const splitter = await page.$('#bowire-sidebar-splitter');
if (splitter) {
  await splitter.hover();
  await page.waitForTimeout(200);
  await page.screenshot({ path: resolve(outDir, 'sidebar-collapsed-hover.png'), fullPage: false });
  console.log('collapsed-hover screenshot saved');
}

await browser.close();
