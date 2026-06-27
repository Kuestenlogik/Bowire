// Reproduce: with 2 source URLs (sample /api/locations + built-in MCP),
// clicking the /api/locations panel header opens the MCP panel instead.
import { chromium } from '@playwright/test';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('console', m => { if (m.type() === 'error') console.log('[err]', m.text()); });

await page.goto('http://localhost:5180/');
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });

// Set up: clear, create workspace, add the sample URL
await page.evaluate(() => {
  localStorage.clear();
  const id = 'demo';
  localStorage.setItem('bowire_workspaces', JSON.stringify([{id, name: 'Demo', color: 'sky', createdAt: Date.now()}]));
  localStorage.setItem('bowire_active_workspace_id', id);
  // Add the sample's /api/locations URL + ensure MCP shows up too
  const wsKey = (k) => 'bowire_ws_' + id + '_' + k.replace(/^bowire_/, '');
  localStorage.setItem(wsKey('bowire_server_urls'),
    JSON.stringify(['http://localhost:5181/api/locations']));
});
await page.reload();
await page.waitForSelector('#bowire-app.bowire-app-ready');

// Go to Discover
await page.click('[data-rail-mode-id="discover"]');
await page.waitForTimeout(800);

// Inspect the source panels in the sidebar
const dump = await page.evaluate(() => {
  const panels = Array.from(document.querySelectorAll('.bowire-source-panel'));
  return panels.map(p => ({
    id: p.id,
    originUrl: p.getAttribute('data-origin-url'),
    label: p.querySelector('.bowire-source-panel-label')?.textContent || '(no label)',
    expanded: p.classList.contains('expanded'),
    headerId: p.querySelector('.bowire-source-panel-header')?.id,
  }));
});
console.log('--- panels found:');
console.log(JSON.stringify(dump, null, 2));

// Click the /api/locations panel header
const locPanel = dump.find(p => (p.originUrl || '').includes('/api/locations'));
if (locPanel) {
  console.log('\n--- clicking panel:', locPanel.id);
  await page.click('#' + locPanel.headerId);
  await page.waitForTimeout(400);
  const after = await page.evaluate(() => {
    return Array.from(document.querySelectorAll('.bowire-source-panel')).map(p => ({
      id: p.id, expanded: p.classList.contains('expanded'),
      originUrl: p.getAttribute('data-origin-url'),
    }));
  });
  console.log('--- after click:');
  console.log(JSON.stringify(after, null, 2));
}

await browser.close();
