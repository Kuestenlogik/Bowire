import { test, expect } from '@playwright/test';
import { bootFresh, readWorkspaces } from './helpers';

/**
 * Phase 1 — Empty state (first run / no workspace).
 *
 * Mirrors docs/testing/manual-walkthrough.md § Phase 1. Asserts:
 *  - first-run welcome card with the canonical primary CTA renders,
 *  - the topbar carries no workspace chip (no workspace exists),
 *  - clicking a non-home rail does NOT snap back to home
 *    (force-home rule was retired),
 *  - the Settings cog opens the settings dialog with the expected tree.
 */
test.describe('Phase 1 — empty state', () => {
    test.beforeEach(async ({ page }) => {
        await bootFresh(page);
    });

    test('welcome card + primary CTA visible, no workspace chip, no workspaces persisted', async ({ page }) => {
        // Welcome card landing band — the `bowire-home-band-firstrun`
        // wrapper only renders when workspaces.length === 0.
        await expect(page.locator('.bowire-home-band-firstrun')).toBeVisible();
        await expect(page.locator('.bowire-empty-card-headline'))
            .toHaveText('Create your first workspace');
        // Stable id wired in render-main.js (#281) — tour engine also
        // targets this selector, so renames are doubly-gated.
        await expect(page.locator('#bowire-welcome-create-btn')).toBeVisible();
        await expect(page.locator('#bowire-welcome-create-btn')).toHaveText('New workspace…');
        // Take-a-tour secondary CTA from the same renderEmptyCard call.
        await expect(page.locator('#bowire-welcome-tour-btn')).toBeVisible();

        // No workspace exists → no chip on the topbar. The chip's id
        // is only present when render-env-auth.js painted it; absence
        // here proves the no-workspace branch ran.
        await expect(page.locator('#bowire-workspace-chip')).toHaveCount(0);

        // localStorage has not been seeded with a workspace yet.
        expect(await readWorkspaces(page)).toEqual([]);
    });

    test('non-home rail click switches rail (force-home rule retired)', async ({ page }) => {
        // Discover rail is in the always-on set per the walkthrough.
        // Click should flip railMode AND the rail-strip's `.active`
        // class — the force-home regression would visibly toggle and
        // then revert before paint.
        const discoverBtn = page.locator('.bowire-rail-btn[data-rail-mode-id="discover"]');
        await expect(discoverBtn).toBeVisible();
        await discoverBtn.click();
        await expect(discoverBtn).toHaveClass(/active/);

        // The Discover rail's no-workspace empty state should now
        // own the main pane — NOT the home welcome band. We don't
        // assert the headline string (each rail's copy is in flux);
        // the absence of `.bowire-home-band-firstrun` is the
        // load-bearing assertion here.
        await expect(page.locator('.bowire-home-band-firstrun')).toHaveCount(0);

        // Persisted rail mode should update too — guards the case
        // where the in-memory variable flips but localStorage stays
        // stuck on 'home'.
        const persistedMode = await page.evaluate(() => localStorage.getItem('bowire_rail_mode'));
        expect(persistedMode).toBe('discover');
    });

    test('clicking the welcome CTA opens the create-workspace dialog', async ({ page }) => {
        await page.locator('#bowire-welcome-create-btn').click();
        // Dialog overlay + name input both come from
        // openCreateWorkspaceDialog. The dialog uses the shared
        // `.bowire-confirm-overlay` shell with a `.bowire-ws-create-dialog`
        // marker class.
        await expect(page.locator('.bowire-ws-create-dialog')).toBeVisible();
        await expect(page.locator('#bowire-ws-create-title')).toHaveText('Create workspace');
        // Esc closes without persisting anything.
        await page.keyboard.press('Escape');
        await expect(page.locator('.bowire-ws-create-dialog')).toHaveCount(0);
        expect(await readWorkspaces(page)).toEqual([]);
    });

    test('settings cog opens settings dialog with the user-prefs tree', async ({ page }) => {
        await page.locator('.bowire-rail-settings').click();
        await expect(page.locator('.bowire-settings-overlay')).toBeVisible();
        // Settings header carries the literal title; close button has
        // the stable id from settings.js.
        await expect(page.locator('.bowire-settings-title')).toHaveText('Settings');
        await expect(page.locator('#bowire-settings-close-btn')).toBeVisible();
        // The right-panel id encodes the active tab. Boot default
        // is General, so the panel id should reflect that.
        await expect(page.locator('#bowire-settings-right-general')).toBeVisible();
        // Esc closes — guards the listener bound by renderSettingsDialog.
        await page.keyboard.press('Escape');
        // closeSettings tears the overlay down completely.
        await expect(page.locator('.bowire-settings-overlay')).toHaveCount(0);
    });
});
