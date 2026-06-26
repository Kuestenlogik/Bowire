import { chromium } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

const outDir = resolve('artifacts/screenshots');
mkdirSync(outDir, { recursive: true });

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
await page.goto('http://localhost:5180/');
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });
await page.evaluate(() => {
  const id = 'ws-demo';
  localStorage.setItem('bowire_workspaces', JSON.stringify([{ id, name: 'Demo', color: 'sky', createdAt: Date.now() }]));
  localStorage.setItem('bowire_active_workspace_id', id);
});
await page.reload();
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });

// Crop to just the rail
const rail = await page.$('#bowire-activity-rail');
await rail.screenshot({ path: resolve(outDir, 'rail-with-home-divider.png') });
console.log('rail screenshot saved');

await browser.close();
