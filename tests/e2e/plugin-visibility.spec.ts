import { test, expect } from '@playwright/test';
import { bootFresh } from './helpers';

/**
 * Hiding a protocol from your own sidebar (#638).
 *
 * The point of running this in a browser rather than asserting the store:
 * the store already has unit coverage, and what it cannot tell us is whether
 * the row actually moves, whether the way back is on the page, and whether
 * the choice survives a reload. A preference whose undo nobody can find is a
 * bug with a nice name, and only this suite can catch that.
 *
 * A fresh CI instance loads the bundled protocols (gRPC, REST, MQTT, …), so
 * there is always at least one row to act on. The spec takes whichever comes
 * first rather than naming one — which protocols ship is not what is under
 * test here.
 */
test.describe('Per-identity protocol visibility (#638)', () => {
    test.beforeEach(async ({ page }) => {
        await bootFresh(page);
    });

    async function openProtocols(page: import('@playwright/test').Page) {
        await page.locator('.bowire-rail-settings').click();
        await expect(page.locator('.bowire-settings-overlay')).toBeVisible();
        // The Plugins group header navigates to Protocols itself — its own
        // comment calls it "the most common entry" — so this needs no tree
        // expansion, which is what the Protocols leaf would have needed.
        await page.locator('.bowire-settings-left').getByText('Plugins', { exact: true }).click();
        await expect(page.locator('#bowire-settings-right-configure-protocols')).toBeVisible();
    }

    test('a protocol can be hidden, found again, and shown', async ({ page }) => {
        await openProtocols(page);

        const rows = page.locator('.bowire-settings-plugin-row-with-lifecycle');
        const before = await rows.count();
        test.skip(before === 0, 'no protocol plugins loaded in this instance');

        // Hover-reveal: the control is display:none until the pointer is on
        // the row, which is also the assertion that it is not a wall of
        // buttons down the page.
        const firstRow = rows.first();
        const hide = firstRow.locator('.bowire-settings-plugin-hide-toggle');
        await expect(hide).toBeHidden();
        await firstRow.hover();
        await expect(hide).toBeVisible();

        await hide.click();

        // The row moved rather than vanished: the disclosure names how many
        // are behind it, which is the answer to "where did MQTT go".
        const disclosure = page.locator('.bowire-settings-hidden-disclosure');
        await expect(disclosure).toBeVisible();
        await expect(disclosure).toContainText('hidden protocol');

        // Hiding expands the section, so the way back is already on screen.
        const hiddenBox = page.locator('.bowire-settings-hidden-protocols');
        await expect(hiddenBox).toBeVisible();
        const show = hiddenBox.locator('.bowire-settings-plugin-hide-toggle').first();
        await expect(show).toHaveText('Show');

        await show.click();

        await expect(page.locator('.bowire-settings-hidden-disclosure')).toHaveCount(0);
        await expect(rows).toHaveCount(before);
    });

    test('the choice survives a reload', async ({ page }) => {
        await openProtocols(page);

        const rows = page.locator('.bowire-settings-plugin-row-with-lifecycle');
        test.skip(await rows.count() === 0, 'no protocol plugins loaded in this instance');

        const firstRow = rows.first();
        await firstRow.hover();
        await firstRow.locator('.bowire-settings-plugin-hide-toggle').click();
        await expect(page.locator('.bowire-settings-hidden-disclosure')).toBeVisible();

        // Through the file in the identity's slot and back — the half a
        // component test cannot reach.
        await page.reload();
        await openProtocols(page);

        await expect(page.locator('.bowire-settings-hidden-disclosure')).toBeVisible({ timeout: 10_000 });

        // Leave the instance as we found it: the suite shares one workbench.
        await page.locator('.bowire-settings-hidden-disclosure').click();
        const hiddenBox = page.locator('.bowire-settings-hidden-protocols');
        await hiddenBox.locator('.bowire-settings-plugin-hide-toggle').first().click();
        await expect(page.locator('.bowire-settings-hidden-disclosure')).toHaveCount(0);
    });
});
