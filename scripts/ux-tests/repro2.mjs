import { chromium } from '@playwright/test';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });

await page.goto('http://localhost:5180/');
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });
await page.evaluate(() => {
  localStorage.clear();
  const id = 'demo';
  localStorage.setItem('bowire_workspaces', JSON.stringify([{id, name: 'Demo', color: 'sky', createdAt: Date.now()}]));
  localStorage.setItem('bowire_active_workspace_id', id);
  // CORRECT wsKey format: bowire_ws_<id>_<key-without-bowire-prefix>
  localStorage.setItem('bowire_ws_demo_server_urls',
    JSON.stringify(['http://localhost:5181/api/locations']));
});
await page.reload();
await page.waitForSelector('#bowire-app.bowire-app-ready');
await page.click('[data-rail-mode-id="discover"]');
await page.waitForTimeout(3000); // wait for discovery

const dump = await page.evaluate(() => ({
  storedUrls: localStorage.getItem('bowire_ws_demo_server_urls'),
  sidebarLen: document.getElementById('bowire-sidebar')?.outerHTML?.length || 0,
  panels: Array.from(document.querySelectorAll('.bowire-source-panel')).map(p => ({
    id: p.id, originUrl: p.getAttribute('data-origin-url'),
    label: p.querySelector('.bowire-source-panel-label')?.textContent,
    expanded: p.classList.contains('expanded'),
    headerId: p.querySelector('.bowire-source-panel-header')?.id,
    childServiceGroups: p.querySelectorAll('.bowire-service-group').length,
  })),
}));
console.log(JSON.stringify(dump, null, 2));
await browser.close();
