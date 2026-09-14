import { test, expect, type Page } from '@playwright/test';

/**
 * Cross-tab pane split (#250, Phase 1b).
 *
 * Two panes side by side, each with its own tab strip and its own live
 * state (#695). What these check is the part an operator sees: the split
 * opens beside the tab and carries it over, both panes keep streaming at
 * once, the pane under the pointer is the focused one, the layout comes
 * back after a reload, and closing the last tab of a pane folds the split
 * back into one.
 *
 * Driven by the in-repo SSE sample (:5186): Ticker (one frame a second)
 * and Status report, both server-streaming.
 */

const SSE_WORKBENCH = 'http://localhost:5186/bowire';

test.setTimeout(90_000);

async function openWorkbench(page: Page): Promise<void> {
    await page.goto(SSE_WORKBENCH, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });
    // The sample persists the strip and the panes per browser profile;
    // start from one pane and no tabs.
    await page.evaluate(() => {
        for (const k of Object.keys(localStorage)) {
            if (/bowire_(request_tabs|panes)$/.test(k)) localStorage.removeItem(k);
        }
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });
}

const method = (page: Page, name: string) => page.locator('.bowire-method-item', { hasText: name }).first();
const panes = (page: Page) => page.locator('.bowire-tab-pane');
const pane = (page: Page, id: string) => page.locator(`.bowire-tab-pane[data-pane-id="${id}"]`);

/** Open Ticker, start it, open Status report beside it (a live tab is not reused). */
async function twoTabsOneStreaming(page: Page): Promise<void> {
    await method(page, 'Ticker').click();
    await page.locator('#bowire-action-execute-btn').click();
    await page.waitForFunction(
        () => document.querySelectorAll('.bowire-stream-list-item').length >= 2,
        null, { timeout: 30_000 });
    await method(page, 'Status report').click();
    await expect(page.locator('.bowire-request-tab')).toHaveCount(2);
}

test.describe('Pane split (#250)', () => {
    test('Split right opens a second pane carrying the tab, and both panes stream at once', async ({ page }) => {
        await openWorkbench(page);
        await twoTabsOneStreaming(page);

        // One pane renders straight into main — no pane columns yet.
        await expect(panes(page)).toHaveCount(0);

        await page.locator('.bowire-request-tab-split').first().click();
        await expect(panes(page)).toHaveCount(2);
        await expect(page.locator('#bowire-panes-divider')).toHaveCount(1);
        // The active tab (Status report) moved over; Ticker stayed.
        await expect(pane(page, 'pane_1').locator('.bowire-request-tab')).toHaveCount(1);
        await expect(pane(page, 'pane_2').locator('.bowire-request-tab')).toHaveCount(1);
        await expect(pane(page, 'pane_2').locator('.bowire-request-tab')).toContainText('Status report');
        await expect(pane(page, 'pane_2')).toHaveClass(/focused/);

        // Start the second stream in the second pane. Its ids carry the
        // pane, so the first pane's buttons keep the ids they always had.
        await pane(page, 'pane_2').locator('#bowire-action-execute-btn-pane_2').click();
        await expect(pane(page, 'pane_2').locator('#bowire-action-stop-btn-pane_2')).toBeVisible();

        const frames = () => page.evaluate(() =>
            [...document.querySelectorAll('.bowire-tab-pane')].map(p => p.querySelectorAll('.bowire-stream-list-item').length));
        const before = await frames();
        await page.waitForTimeout(3_000);
        const after = await frames();
        expect(after[0], `pane 1 kept streaming (${before[0]} → ${after[0]})`).toBeGreaterThan(before[0]);
        expect(after[1], `pane 2 streams (${before[1]} → ${after[1]})`).toBeGreaterThan(before[1]);

        // One state badge per pane, each counting its own frames.
        await expect(page.locator('#bowire-stream-state-badge')).toHaveCount(1);
        await expect(page.locator('#bowire-stream-state-badge-pane_2')).toHaveCount(1);
    });

    test('focus follows the pointer, and the layout survives a reload', async ({ page }) => {
        await openWorkbench(page);
        await twoTabsOneStreaming(page);
        await page.locator('.bowire-request-tab-split').first().click();
        await expect(panes(page)).toHaveCount(2);
        await expect(pane(page, 'pane_2')).toHaveClass(/focused/);

        await pane(page, 'pane_1').locator('.bowire-request-tab').first().click();
        await expect(pane(page, 'pane_1')).toHaveClass(/focused/);
        await expect(pane(page, 'pane_2')).not.toHaveClass(/focused/);

        await page.reload({ waitUntil: 'domcontentloaded' });
        await page.waitForSelector('#bowire-app', { timeout: 30_000 });
        await expect(panes(page)).toHaveCount(2);
        await expect(pane(page, 'pane_1').locator('.bowire-request-tab')).toContainText('Ticker');
        await expect(pane(page, 'pane_2').locator('.bowire-request-tab')).toContainText('Status report');
        await expect(pane(page, 'pane_1')).toHaveClass(/focused/);
    });

    test('closing the last tab of a pane folds the split back into one pane', async ({ page }) => {
        await openWorkbench(page);
        await twoTabsOneStreaming(page);
        await page.locator('.bowire-request-tab-split').first().click();
        await expect(panes(page)).toHaveCount(2);

        const tab2 = pane(page, 'pane_2').locator('.bowire-request-tab').first();
        await tab2.hover();
        await tab2.locator('.bowire-request-tab-close').click();

        await expect(panes(page)).toHaveCount(0);
        await expect(page.locator('.bowire-request-tab')).toHaveCount(1);
        await expect(page.locator('.bowire-request-tab').first()).toContainText('Ticker');
        // The survivor is the tab in front again — its stream is still on screen.
        await expect(page.locator('#bowire-action-stop-btn')).toBeVisible();
    });

    test('Ctrl+\\ splits, and with two panes moves the tab over', async ({ page }) => {
        await openWorkbench(page);
        await twoTabsOneStreaming(page);

        await page.keyboard.press('Control+\\');
        await expect(panes(page)).toHaveCount(2);
        await expect(pane(page, 'pane_2').locator('.bowire-request-tab')).toContainText('Status report');

        // Again: the tab in front (Status report, pane 2) moves back to
        // pane 1, which empties pane 2 — the split folds.
        await page.keyboard.press('Control+\\');
        await expect(panes(page)).toHaveCount(0);
        await expect(page.locator('.bowire-request-tab')).toHaveCount(2);
    });
});
