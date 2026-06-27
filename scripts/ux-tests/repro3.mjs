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
  localStorage.setItem('bowire_ws_demo_server_urls', JSON.stringify(['http://localhost:5181/api/locations']));
});
await page.reload();
await page.waitForSelector('#bowire-app.bowire-app-ready');
await page.click('[data-rail-mode-id="discover"]');
await page.waitForTimeout(3000);

const info = await page.evaluate(() => ({
  sidebarById: !!document.getElementById('bowire-sidebar'),
  sidebarsByClass: document.querySelectorAll('.bowire-sidebar').length,
  bodyChildren: Array.from(document.getElementById('bowire-app-body')?.children || []).map(c => ({ tag: c.tagName, id: c.id, cls: c.className })),
  sourcesPanels: document.querySelectorAll('.bowire-source-panel').length,
  servicesList: document.querySelectorAll('.bowire-service-list').length,
}));
console.log(JSON.stringify(info, null, 2));
await browser.close();
