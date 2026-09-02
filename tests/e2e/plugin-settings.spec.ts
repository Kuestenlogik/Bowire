import { test, expect } from '@playwright/test';
import { bootFresh, createWorkspaceViaDialog } from './helpers';

/**
 * Plugin settings actually going somewhere (#640).
 *
 * The bug this guards against is specific and was live for four plugins: the
 * control rendered, the value persisted across reloads, and the plugin that
 * declared it never saw a thing. A component test cannot catch that — the
 * value *was* being stored, just in the wrong place. Only a round trip can,
 * which is why this asks the server what it has rather than reading the
 * control back.
 */
test.describe('Plugin settings round-trip (#640)', () => {
    test.beforeEach(async ({ page }) => {
        await bootFresh(page);
    });

    async function openPlugins(page: import('@playwright/test').Page) {
        await page.locator('.bowire-rail-settings').click();
        await expect(page.locator('.bowire-settings-overlay')).toBeVisible();
        await page.locator('.bowire-settings-left').getByText('Plugins', { exact: true }).click();
        await expect(page.locator('#bowire-settings-right-configure-protocols')).toBeVisible();
    }

    test('with no workspace, the page says where the value would go', async ({ page }) => {
        // Settings are workspace-scoped. Accepting input with nowhere to put
        // it is precisely the failure #640 removed, so the absence of a
        // workspace has to be visible rather than silent.
        await openPlugins(page);

        const note = page.locator('.bowire-settings-help', { hasText: 'saved per workspace' });
        const anySetting = page.locator('[id^="bowire-plugin-setting-"]');

        // Either there is a plugin with settings and the note is shown, or
        // this build ships none and there is nothing to say.
        if (await anySetting.count() > 0) {
            await expect(note.first()).toBeVisible();
        }
    });

    test('a value set in a workspace is on the server, not just in the browser', async ({ page }) => {
        await page.locator('#bowire-welcome-create-btn').click();
        await createWorkspaceViaDialog(page, 'Settings Probe', 'empty');
        await expect(page.locator('.bowire-ws-create-dialog')).toHaveCount(0);

        await openPlugins(page);

        const numberInput = page.locator('input[id^="bowire-plugin-setting-"][type="number"]').first();
        test.skip(await numberInput.count() === 0, 'no plugin in this build declares a number setting');

        // The id carries the plugin and the key, which is what the assertion
        // below needs to ask the server about.
        const id = await numberInput.getAttribute('id') ?? '';
        const [pluginId, key] = id.replace('bowire-plugin-setting-', '').split(/-(.+)/);

        await numberInput.fill('11');
        await numberInput.blur();

        // Ask the server directly. Reading the control back would pass even
        // for the bug: localStorage remembered too.
        await expect.poll(async () => {
            const workspaces = await page.evaluate(() => {
                try { return JSON.parse(localStorage.getItem('bowire_workspaces') || '[]'); }
                catch { return []; }
            }) as Array<{ id: string }>;
            const wsId = workspaces[0]?.id ?? '';
            const body = await page.request.get(
                `/api/plugins/settings?workspaceId=${encodeURIComponent(wsId)}`).then(r => r.json());
            return body?.settings?.[pluginId]?.[key] ?? null;
        }, { timeout: 10_000 }).toBe('11');

        // And it comes back on a fresh page, from the server rather than from
        // whatever this browser happens to be holding.
        await page.reload({ waitUntil: 'domcontentloaded' });
        await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20_000 });
        await openPlugins(page);

        await expect(page.locator(`[id="${id}"]`)).toHaveValue('11');
    });
});
