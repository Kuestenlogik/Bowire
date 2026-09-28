import { test, expect, type Page } from '@playwright/test';
import { bootFresh } from './helpers';

/**
 * A failed /api/protocols is said, not swallowed (#752).
 *
 * The fetch used to be `if (resp.ok) protocols = …` inside a bare catch, so
 * any refusal left the list empty with nothing said, and the Protocols page
 * then read exactly like "no plugins installed". That is how the browser
 * suite came to skip plugin-visibility in every full run (#740): the host
 * throttles /api/* per client, the suite reached the limit, and a 429 turned
 * into a silent empty page. A person who uses the workbench for a while can
 * reach the same limit.
 *
 * The refusal is simulated with route interception rather than by actually
 * exhausting the limiter — the suite runs with it switched off, and the
 * question here is what the page does with a 429, not whether the server
 * sends one.
 *
 * That a later refusal keeps a list an earlier pass loaded is pinned in
 * wwwroot-js/protocols-response.test.mjs: the workbench has no control that
 * starts a second discovery pass after a successful one without going
 * through a workspace URL, and the rule is one small function.
 */

const PROTOCOLS = '**/api/protocols';

async function openProtocols(page: Page): Promise<void> {
    await page.locator('.bowire-rail-settings').click();
    await expect(page.locator('.bowire-settings-overlay')).toBeVisible();
    await page.locator('.bowire-settings-left').getByText('Plugins', { exact: true }).click();
    await expect(page.locator('#bowire-settings-right-configure-protocols')).toBeVisible();
}

/** Answer /api/protocols the way the throttle does: 429 with a Retry-After. */
async function throttleProtocols(page: Page): Promise<void> {
    await page.route(PROTOCOLS, route => route.fulfill({
        status: 429,
        headers: { 'Retry-After': '42' },
        body: '',
    }));
}

const banner = (page: Page) => page.locator('.bowire-settings-protocols-load-error');
const rows = (page: Page) => page.locator('.bowire-settings-plugin-row-with-lifecycle');
const noPluginsCard = (page: Page) =>
    page.locator('#bowire-settings-right-configure-protocols .bowire-settings-section-empty');

test.describe('The protocol list when the server refuses it (#752)', () => {
    test('a 429 is named, with the wait, and never passed off as "no plugins"', async ({ page }) => {
        await throttleProtocols(page);
        await bootFresh(page);
        await openProtocols(page);

        await expect(banner(page)).toBeVisible();
        // The throttle's own Retry-After, so the operator knows how long.
        await expect(banner(page)).toContainText('42');
        // The one thing this page must not claim after a refused request.
        await expect(noPluginsCard(page)).toHaveCount(0);
        await expect(rows(page)).toHaveCount(0);
    });

    test('"Try again" loads the list once the server answers, and the banner goes', async ({ page }) => {
        await throttleProtocols(page);
        await bootFresh(page);
        await openProtocols(page);
        await expect(banner(page)).toBeVisible();

        await page.unroute(PROTOCOLS);
        await banner(page).locator('.bowire-settings-protocols-load-retry').click();

        await expect(rows(page).first()).toBeVisible({ timeout: 20_000 });
        await expect(banner(page)).toHaveCount(0);
    });
});
