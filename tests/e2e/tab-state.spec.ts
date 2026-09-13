import { test, expect, type Page } from '@playwright/test';

/**
 * Per-tab request and response state (#695).
 *
 * Before this, the workbench held one method's request and response in
 * module-level variables, and switching tabs swapped the one set out and
 * back in. Two tabs could never both be live: opening a second tab cleared
 * the first one's response, and a stream kept running into whichever tab
 * happened to be in front. These specs are the observable half of the
 * acceptance list — the state model itself has no UI, so what can be
 * checked is that two tabs keep two responses, and that a stream keeps
 * filling the tab it started in while another one is active.
 *
 * Driven by the in-repo SSE sample (:5186), which hosts the workbench at
 * /bowire with its endpoints already discovered: a one-tick-per-second
 * Ticker, and a Status report.
 */

const SSE_WORKBENCH = 'http://localhost:5186/bowire';

test.setTimeout(90_000);

async function openWorkbench(page: Page): Promise<void> {
    await page.goto(SSE_WORKBENCH, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });
    // A clean strip: the sample persists tabs per browser profile, and a
    // leftover tab from another spec would change which tab gets reused.
    await page.evaluate(() => {
        for (const k of Object.keys(localStorage)) {
            if (k.endsWith('_bowire_request_tabs')) localStorage.removeItem(k);
        }
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });
}

function method(page: Page, name: string) {
    return page.locator('.bowire-method-item', { hasText: name }).first();
}

function tabs(page: Page) {
    return page.locator('.bowire-request-tab:not(.bowire-request-tab-empty)');
}

async function frameCount(page: Page): Promise<number> {
    return page.locator('.bowire-stream-list-item').count();
}

test.describe('Per-tab state (#695)', () => {
    test('a stream keeps filling its own tab while another tab is active', async ({ page }) => {
        await openWorkbench(page);

        await method(page, 'Ticker').click();
        await page.locator('#bowire-action-execute-btn').click();
        await page.waitForFunction(
            () => document.querySelectorAll('.bowire-stream-list-item').length >= 3,
            null, { timeout: 30_000 });
        const before = await frameCount(page);

        // Clicking another method while a stream is live opens it beside
        // the streaming tab rather than over it — the stream's state is
        // the tab, and reusing the tab would have nowhere to put it.
        await method(page, 'Status report').click();
        await expect(tabs(page)).toHaveCount(2);
        await expect(tabs(page).nth(1)).toHaveClass(/active/);
        // The second tab starts empty: no frames, no response from the first.
        expect(await frameCount(page)).toBe(0);

        // Let the ticker run in the background, then come back to it.
        await page.waitForTimeout(3_500);
        await tabs(page).nth(0).click();
        await expect(page.locator('#bowire-stream-output')).toBeVisible();
        const after = await frameCount(page);
        expect(after, `frames kept arriving while the tab was in the background (${before} → ${after})`)
            .toBeGreaterThan(before + 1);
    });

    test('two tabs keep two responses, and a tab clicked again keeps its own', async ({ page }) => {
        await openWorkbench(page);

        // Tab 1: run the ticker briefly and stop it, so the tab holds a
        // finished stream with frames in it.
        await method(page, 'Ticker').click();
        await page.locator('#bowire-action-execute-btn').click();
        await page.waitForFunction(
            () => document.querySelectorAll('.bowire-stream-list-item').length >= 2,
            null, { timeout: 30_000 });
        await page.locator('#bowire-action-stop-btn').click();
        await expect(page.locator('#bowire-action-execute-btn')).toBeVisible();
        const tab1Frames = await frameCount(page);
        expect(tab1Frames).toBeGreaterThanOrEqual(2);

        // Tab 2, explicitly new: a different method with nothing run yet.
        await method(page, 'Status report').click({ modifiers: ['Control'] });
        await expect(tabs(page)).toHaveCount(2);
        expect(await frameCount(page)).toBe(0);

        // Back to tab 1: its frames are still there. The old swap cleared
        // the response on every switch unless a channel was open.
        await tabs(page).nth(0).click();
        await expect.poll(() => frameCount(page)).toBe(tab1Frames);

        // Clicking the very method the tab already shows keeps the tab's
        // state — it is the same method, not another one.
        await method(page, 'Ticker').click();
        await expect(tabs(page)).toHaveCount(2);
        await expect.poll(() => frameCount(page)).toBe(tab1Frames);

        // Closing tab 2 leaves tab 1 exactly as it was.
        await tabs(page).nth(1).hover();
        await tabs(page).nth(1).locator('.bowire-request-tab-close').click();
        await expect(tabs(page)).toHaveCount(1);
        await expect.poll(() => frameCount(page)).toBe(tab1Frames);
    });

    test('a tab reused for another method starts that method fresh', async ({ page }) => {
        await openWorkbench(page);

        await method(page, 'Ticker').click();
        await page.locator('#bowire-action-execute-btn').click();
        await page.waitForFunction(
            () => document.querySelectorAll('.bowire-stream-list-item').length >= 2,
            null, { timeout: 30_000 });
        await page.locator('#bowire-action-stop-btn').click();
        await expect(page.locator('#bowire-action-execute-btn')).toBeVisible();

        // Nothing live any more, so a plain click reuses the tab — and the
        // other method must not inherit the ticker's frames.
        await method(page, 'Status report').click();
        await expect(tabs(page)).toHaveCount(1);
        expect(await frameCount(page)).toBe(0);
    });
});
