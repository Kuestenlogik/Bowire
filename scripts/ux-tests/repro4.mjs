import { chromium } from '@playwright/test';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('console', m => { console.log('[' + m.type() + ']', m.text()); });

await page.goto('http://localhost:5180/');
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });
await page.evaluate(() => {
  localStorage.clear();
  const id = 'demo';
  localStorage.setItem('bowire_workspaces', JSON.stringify([{id, name: 'Demo', color: 'sky', createdAt: Date.now()}]));
  localStorage.setItem('bowire_active_workspace_id', id);
  localStorage.setItem('bowire_ws_demo_server_urls', JSON.stringify(['http://localhost:5181/api/locations']));
});
await page.reload();
await page.waitForSelector('#bowire-app.bowire-app-ready');
await page.click('[data-rail-mode-id="discover"]');
await page.waitForTimeout(4000);

const html = await page.evaluate(() => {
  const sb = document.querySelector('.bowire-sidebar');
  return sb ? sb.outerHTML.substring(0, 4000) : '(no sidebar)';
});
console.log('---SIDEBAR HTML---');
console.log(html);
await browser.close();
