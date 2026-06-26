import { test, expect } from '@playwright/test';
import {
    bootFresh,
    createWorkspaceViaDialog,
    readActiveWorkspaceId,
    readWorkspaceKey,
    readWorkspaces
} from './helpers';

/**
 * Phase 2 — Create workspace.
 *
 * Covers the create-workspace dialog (validation, template list),
 * the REST template's localStorage seed, and the Empty template's
 * clean-slate behaviour. Mirrors docs/testing/manual-walkthrough.md
 * § Phase 2.
 *
 * Note on REST template: workspace-templates.js does a
 * window.location.reload() after seeding so localStorage rehydrates
 * the in-memory state. We don't drive discovery here — that's a
 * network-dependent assertion better left to Phase 4. Phase 2's
 * REST-template guarantee is "Petstore URL lands in the workspace's
 * serverUrls bucket", which we verify directly from localStorage.
 */
test.describe('Phase 2 — create workspace', () => {
    test.beforeEach(async ({ page }) => {
        await bootFresh(page);
    });

    test('empty-name Create stays open + no workspace persisted', async ({ page }) => {
        await page.locator('#bowire-welcome-create-btn').click();
        await expect(page.locator('.bowire-ws-create-dialog')).toBeVisible();

        // Click Create with an empty name — the dialog should stay
        // open and flash the input red (commit() rejects empty
        // strings). We don't time the red flash; the load-bearing
        // assertion is that the dialog is still here + nothing got
        // persisted.
        await page.locator('.bowire-ws-create-dialog .bowire-confirm-btn:not(.cancel)').click();
        await expect(page.locator('.bowire-ws-create-dialog')).toBeVisible();
        expect(await readWorkspaces(page)).toEqual([]);
    });

    test('Empty template creates a clean-slate workspace', async ({ page }) => {
        await page.locator('#bowire-welcome-create-btn').click();
        await createWorkspaceViaDialog(page, 'Empty Test', 'empty');

        // Empty template skips the reload — workspace-templates.js
        // calls render() instead. Wait for the dialog to disappear
        // (commit() removes the overlay before invoking onCreated).
        await expect(page.locator('.bowire-ws-create-dialog')).toHaveCount(0);

        const workspaces = await readWorkspaces(page);
        expect(workspaces.length).toBe(1);
        const ws = workspaces[0] as { id: string; name: string };
        expect(ws.name).toBe('Empty Test');

        // Empty template apply() is a no-op — no urls, no globals,
        // no collections seeded under the new workspace's wsKey.
        expect(await readWorkspaceKey(page, ws.id, 'server_urls')).toBeNull();
        expect(await readWorkspaceKey(page, ws.id, 'collections')).toBeNull();
        expect(await readWorkspaceKey(page, ws.id, 'global_vars')).toBeNull();

        // Active workspace pointer flips to the new id.
        expect(await readActiveWorkspaceId(page)).toBe(ws.id);
    });

    test('REST template seeds Petstore URL + starter collection', async ({ page }) => {
        await page.locator('#bowire-welcome-create-btn').click();
        await createWorkspaceViaDialog(page, 'Petstore Test', 'rest');

        // REST template fires window.location.reload() after seeding.
        // Wait for the app shell to come back up post-reload.
        await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20_000 });

        const workspaces = await readWorkspaces(page);
        expect(workspaces.length).toBe(1);
        const ws = workspaces[0] as { id: string; name: string };
        expect(ws.name).toBe('Petstore Test');

        // serverUrls seed — workspace-templates.js writes
        // ['https://petstore.swagger.io/v2'] under the new
        // workspace's bucket.
        const urls = await readWorkspaceKey(page, ws.id, 'server_urls') as string[] | null;
        expect(urls).not.toBeNull();
        expect(urls![0]).toContain('petstore.swagger.io');

        // Starter collection lands with a non-empty items list.
        const collections = await readWorkspaceKey(page, ws.id, 'collections') as
            Array<{ name: string; items: unknown[] }> | null;
        expect(collections).not.toBeNull();
        expect(collections!.length).toBeGreaterThanOrEqual(1);
        expect(collections![0].items.length).toBeGreaterThanOrEqual(1);

        // Topbar workspace chip now exists — proves the active
        // workspace state hydrated post-reload.
        await expect(page.locator('#bowire-workspace-chip')).toBeVisible();
        await expect(page.locator('.bowire-workspace-chip-name')).toHaveText('Petstore Test');
    });

    test('creating a second workspace appends without disturbing the first', async ({ page }) => {
        // First workspace via Empty (no reload, faster) so the test
        // doesn't have to ride two reload cycles to get to its
        // assertion.
        await page.locator('#bowire-welcome-create-btn').click();
        await createWorkspaceViaDialog(page, 'First', 'empty');
        await expect(page.locator('.bowire-ws-create-dialog')).toHaveCount(0);
        expect((await readWorkspaces(page)).length).toBe(1);

        // Open the dialog again — once a workspace exists, the
        // welcome card is gone. Reach the dialog via the workspace-
        // chip menu's "+ New workspace…" entry instead. The chip
        // itself is keyed by id; the menu entries don't have stable
        // ids, but the dialog's selectors are the same as before.
        await page.locator('#bowire-workspace-chip').click();
        // The chip dropdown carries a "+ New workspace…" action. The
        // menu items render as `<div>`s (not buttons) so we can't use
        // role-based lookup — match the action-class + text content
        // instead. The action class is `bowire-workspace-menu-item-action`
        // and there are two such entries; filter by inner label text.
        await page
            .locator('.bowire-workspace-menu .bowire-workspace-menu-item-action')
            .filter({ hasText: 'New workspace…' })
            .first()
            .click();
        await createWorkspaceViaDialog(page, 'Second', 'empty');
        await expect(page.locator('.bowire-ws-create-dialog')).toHaveCount(0);

        const ws = await readWorkspaces(page);
        expect(ws.length).toBe(2);
        const names = (ws as Array<{ name: string }>).map((w) => w.name).sort();
        expect(names).toEqual(['First', 'Second']);
    });
});
