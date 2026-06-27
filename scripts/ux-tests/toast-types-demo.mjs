import { chromium } from '@playwright/test';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 700, height: 500 } });
await page.goto('http://localhost:5180/');
await page.waitForSelector('#bowire-app.bowire-app-ready');
await page.evaluate(() => {
  toast('Saved settings to disk.', 'success', { duration: 0 });
  setTimeout(() => toast('Workspace switched to "Demo".', 'info', { duration: 0 }), 50);
  setTimeout(() => toast('CA certificate expires in 7 days.', 'warning', { duration: 0 }), 100);
  setTimeout(() => toast('Failed to reach https://api.example.com — connection refused.', 'error', { duration: 0 }), 150);
});
await page.waitForTimeout(500);
const c = await page.$('.bowire-toast-container');
if (c) await c.screenshot({ path: 'artifacts/screenshots/toast-types.png' });
await browser.close();
