import { chromium } from '@playwright/test';

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 800 } });

async function state() {
  return await page.evaluate(() => ({
    collapsed: localStorage.getItem('bowire_sidebar_collapsed'),
    appBodyClass: document.getElementById('bowire-app-body')?.className.includes('bowire-sidebar-is-collapsed') ? 'collapsed' : 'expanded',
  }));
}

await page.goto('http://localhost:5180/');
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });
await page.evaluate(() => {
  const id = 'ws-demo';
  localStorage.setItem('bowire_workspaces', JSON.stringify([{ id, name: 'Demo', color: 'sky', createdAt: Date.now() }]));
  localStorage.setItem('bowire_active_workspace_id', id);
});

// === Test 1: expanded → single click on bar → should stay expanded
await page.evaluate(() => localStorage.removeItem('bowire_sidebar_collapsed'));
await page.reload();
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });
await page.click('[data-rail-mode-id="discover"]');
await page.waitForTimeout(200);
let sp = await page.$('#bowire-sidebar-splitter');
let box = await sp.boundingBox();
await page.mouse.click(box.x + box.width / 2, box.y + 60);
await page.waitForTimeout(300);
console.log('TEST 1 expanded + single click on bar (expect: expanded):', await state());

// === Test 2: expanded → dblclick on bar → should collapse
await page.mouse.dblclick(box.x + box.width / 2, box.y + 60);
await page.waitForTimeout(400);
console.log('TEST 2 expanded + dblclick on bar (expect: collapsed):', await state());

// === Test 3: collapsed → single click on bar → should expand
sp = await page.$('#bowire-sidebar-splitter');
box = await sp.boundingBox();
await page.mouse.click(box.x + box.width / 2, box.y + 60);
await page.waitForTimeout(300);
console.log('TEST 3 collapsed + single click on bar (expect: expanded):', await state());

// === Test 4: collapsed → dblclick on bar → should expand (not toggle back)
// Collapse first
await page.evaluate(() => { localStorage.setItem('bowire_sidebar_collapsed', '1'); });
await page.reload();
await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 10000 });
await page.click('[data-rail-mode-id="discover"]');
await page.waitForTimeout(200);
console.log('before TEST 4:', await state());
sp = await page.$('#bowire-sidebar-splitter');
box = await sp.boundingBox();
await page.mouse.dblclick(box.x + box.width / 2, box.y + 60);
await page.waitForTimeout(400);
console.log('TEST 4 collapsed + dblclick on bar (expect: expanded):', await state());

await browser.close();
