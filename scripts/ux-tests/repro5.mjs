import { chromium } from '@playwright/test';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('console', m => { if (m.type() !== 'log') console.log('[' + m.type() + ']', m.text().substring(0, 200)); });

await page.goto('http://localhost:5180/');
await page.waitForSelector('#bowire-app.bowire-app-ready');
await page.evaluate(() => localStorage.clear());
await page.reload();
await page.waitForSelector('#bowire-app.bowire-app-ready');

// Create workspace via UI
await page.evaluate(() => {
  if (typeof createWorkspace === 'function') createWorkspace('Demo');
});
await page.waitForTimeout(500);

// Add URL via UI - call addServerUrl
const addResult = await page.evaluate(() => {
  if (typeof addServerUrl === 'function') {
    addServerUrl('http://localhost:5181/api/locations');
    return 'addServerUrl called';
  }
  return 'no addServerUrl fn';
});
console.log(addResult);
await page.waitForTimeout(3000);

// Switch to Discover
await page.click('[data-rail-mode-id="discover"]');
await page.waitForTimeout(3000);

const dump = await page.evaluate(() => ({
  serverUrlsLen: typeof serverUrls !== 'undefined' ? serverUrls.length : 'undefined',
  servicesLen: typeof services !== 'undefined' ? services.length : 'undefined',
  panels: Array.from(document.querySelectorAll('.bowire-source-panel')).map(p => ({
    id: p.id, originUrl: p.getAttribute('data-origin-url'),
    label: p.querySelector('.bowire-source-panel-label')?.textContent,
  })),
}));
console.log('--- dump:');
console.log(JSON.stringify(dump, null, 2));
await browser.close();
