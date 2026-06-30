import { chromium } from '@playwright/test';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
await page.goto('http://localhost:5180/');
await page.waitForSelector('#bowire-app.bowire-app-ready');
await page.evaluate(() => localStorage.clear());
await page.reload();
await page.waitForSelector('#bowire-app.bowire-app-ready');
await page.waitForTimeout(300);

const info = await page.evaluate(() => {
  const rail = document.getElementById('bowire-activity-rail');
  if (!rail) return 'no rail';
  const children = Array.from(rail.children).map(c => ({
    tag: c.tagName.toLowerCase(),
    cls: c.className,
    id: c.id || null,
    railId: c.dataset.railModeId || null,
    rect: { h: c.getBoundingClientRect().height, visible: c.getBoundingClientRect().height > 0 }
  }));
  return { count: children.length, children };
});
console.log(JSON.stringify(info, null, 2));
await browser.close();
