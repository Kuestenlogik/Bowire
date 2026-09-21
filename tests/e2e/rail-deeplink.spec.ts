import { test, expect, type Page } from '@playwright/test';
import { bootFresh } from './helpers';

/**
 * `?rail=<id>` — the deep link, in a browser (#735).
 *
 * The parameter was documented in three places and read by nothing: the
 * rendered page was character-identical with and without it. The unit test
 * beside this one covers the decision table; what only a browser can show is
 * that the decision survives a real boot — that the rail the link names is
 * the one the rail strip marks active, before anybody clicks anything.
 *
 * That is also why the ticket asked for this test: a deterministic starting
 * surface is what a browser test needs so it does not have to click its way
 * there through translated labels. #733 went looking for exactly this, did
 * not find it, and built `data-tree-node` instead.
 */

const APP = '#bowire-app.bowire-app-ready';

/** The id of the rail the strip currently marks active. */
async function activeRail(page: Page): Promise<string | null> {
    return await page.evaluate(() => {
        const btn = document.querySelector('.bowire-rail-btn.active[data-rail-mode-id]');
        return btn ? btn.getAttribute('data-rail-mode-id') : null;
    });
}

/** Navigate with a query string and wait for the shell to hydrate. */
async function open(page: Page, query: string): Promise<void> {
    await page.goto('/' + query, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector(APP, { timeout: 20_000 });
}

test.describe('The ?rail= deep link', () => {
    test.beforeEach(async ({ page }) => {
        await bootFresh(page);
    });

    test('opens the rail the link names', async ({ page }) => {
        // Asserted against the rail this build actually ships rather than a
        // hard-coded id: a host can compose its own rail set, and a spec
        // that assumes one would fail on the host rather than on the link.
        const shipped = await page.evaluate(
            () => ((window as any).__BOWIRE_CONFIG__?.rails ?? []).map((r: { id: string }) => r.id));
        expect(shipped).toContain('workspaces');

        await open(page, '?rail=workspaces');

        await expect.poll(() => activeRail(page), { timeout: 10_000 }).toBe('workspaces');
    });

    test('wins over the rail the recipient left open', async ({ page }) => {
        // The whole point of the ticket. A link that loses to the
        // recipient's own history does nothing for anybody who has used
        // the workbench before — which is everybody it would be sent to.
        await page.evaluate(() => {
            try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
        });
        await open(page, '?rail=workspaces');

        await expect.poll(() => activeRail(page), { timeout: 10_000 }).toBe('workspaces');
    });

    test('survives the reload, because it is persisted', async ({ page }) => {
        // A shared link that is forgotten on the next F5 is a worse promise
        // than none: the surface moves without anybody touching the rail.
        await open(page, '?rail=workspaces');
        await expect.poll(() => activeRail(page), { timeout: 10_000 }).toBe('workspaces');

        await page.goto('/', { waitUntil: 'domcontentloaded' });
        await page.waitForSelector(APP, { timeout: 20_000 });

        await expect.poll(() => activeRail(page), { timeout: 10_000 }).toBe('workspaces');
    });

    test('says so when the link names a rail this build does not have', async ({ page }) => {
        // Silence here reads as "this is the rail you were sent to" when it
        // is merely the last one open. The id is quoted back so the person
        // can see what was asked for — a typo in a shared link is the
        // ordinary case.
        const before = await activeRail(page);

        await open(page, '?rail=nonesuch');

        const toast = page.locator('.bowire-toast').last();
        await expect(toast).toBeVisible({ timeout: 10_000 });
        await expect(toast).toContainText('nonesuch', { timeout: 5_000 });

        // And it did not move anybody anywhere on the strength of a value
        // it could not resolve.
        expect(await activeRail(page)).toBe(before);
    });
});
