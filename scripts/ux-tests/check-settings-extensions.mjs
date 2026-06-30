import { chromium } from '@playwright/test';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });

await page.goto('http://localhost:5180/');
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });

// Click the gear/settings button
await page.click('button[title="Settings"]').catch(()=>{});
await page.waitForTimeout(800);

// Click Plugins tab
const tabClick = await page.evaluate(() => {
  var btns = Array.from(document.querySelectorAll('button, [role="tab"]'));
  var b = btns.find(x => /^plugins$/i.test((x.textContent||'').trim()));
  if (b) { b.click(); return true; } return false;
});
console.log('plugins tab clicked:', tabClick);
await page.waitForTimeout(600);

const dump = await page.evaluate(() => ({
  hasExtSection: !!document.querySelector('.bowire-settings-plugin-extensions'),
  sectionHTML: document.querySelector('.bowire-settings-plugin-extensions')?.outerHTML?.substring(0, 1500),
  extRowsCount: document.querySelectorAll('.bowire-settings-extension-row').length,
  pluginRowsCount: document.querySelectorAll('.bowire-settings-plugin-row').length,
  pluginRowsLabels: Array.from(document.querySelectorAll('.bowire-settings-plugin-row .bowire-settings-plugin-row-name, .bowire-settings-plugin-row [class*="name"]')).map(n => n.textContent.trim()).slice(0, 25),
}));
console.log(JSON.stringify(dump, null, 2));
await browser.close();
