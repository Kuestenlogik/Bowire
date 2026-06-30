import { chromium } from '@playwright/test';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
await page.goto('http://localhost:5180/');
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });
await page.evaluate(() => localStorage.clear());
await page.reload();
await page.waitForSelector('#bowire-app.bowire-app-ready');
// Start the tour
await page.evaluate(() => { if (typeof tourStartGettingStarted === 'function') tourStartGettingStarted({ force: true }); });
await page.waitForTimeout(400);
// Click Next once to advance to step 1
await page.click('.bowire-tour-btn-primary').catch(()=>{});
await page.waitForTimeout(300);

// Inspect tour button DOM
const html = await page.evaluate(() => {
  const actions = document.querySelector('.bowire-tour-actions');
  return actions ? actions.outerHTML : '(no tour actions found)';
});
console.log(html);

await page.screenshot({ path: 'artifacts/screenshots/tour-buttons-state.png', clip: { x: 100, y: 200, width: 600, height: 500 } });
await browser.close();
