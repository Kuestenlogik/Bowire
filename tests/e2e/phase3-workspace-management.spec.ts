import { test, expect } from '@playwright/test';
import {
    bootFresh,
    createWorkspaceViaDialog,
    readActiveWorkspaceId,
    readWorkspaces
} from './helpers';

/**
 * Phase 3 — Workspace management.
 *
 * Covers the all-workspaces overview list, switching the active
 * workspace via the overview, renaming, duplicating, saving as a
 * user template, and deletion (including the last-workspace case
 * that should land back on the no-workspace state without trapping
 * the operator on Home). Mirrors
 * docs/testing/manual-walkthrough.md § Phase 3.
 *
 * Every test seeds two Empty workspaces (skip-reload, so the
 * setup is fast) before exercising the surface under test.
 */
test.describe('Phase 3 — workspace management', () => {
    test.beforeEach(async ({ page }) => {
        await bootFresh(page);
        // Two Empty workspaces — Empty template's apply() is a no-op,
        // so the create dialog skips the page reload and goes straight
        // to render(). Faster than the REST template and enough to
        // exercise list / overview / switch / rename surfaces.
        await page.locator('#bowire-welcome-create-btn').click();
        await createWorkspaceViaDialog(page, 'Alpha', 'empty');
        await expect(page.locator('.bowire-ws-create-dialog')).toHaveCount(0);
        await page.locator('#bowire-workspace-chip').click();
        await page
            .locator('.bowire-workspace-menu .bowire-workspace-menu-item-action')
            .filter({ hasText: 'New workspace…' })
            .first()
            .click();
        await createWorkspaceViaDialog(page, 'Beta', 'empty');
        await expect(page.locator('.bowire-ws-create-dialog')).toHaveCount(0);
        expect((await readWorkspaces(page)).length).toBe(2);
    });

    test('overview lists both workspaces with active marker', async ({ page }) => {
        // Reach the overview via the workspaces rail + sidebar title.
        await page.locator('.bowire-rail-btn[data-rail-mode-id="workspaces"]').click();
        await expect(page.locator('#bowire-main-workspaces')).toBeVisible();
        // Sidebar title is rendered as a clickable button by
        // renderSidebarToolbar — `onTitleClick` routes to
        // _goToWorkspacesOverview. Match by visible title text since
        // renderSidebarToolbar doesn't expose a stable id.
        await page.locator('.bowire-sidebar-mode').getByTitle('Open Workspaces overview').click();

        // The overview header carries `Workspaces (2)` — count is
        // assembled from workspaces.length so this proves both
        // workspaces are listed.
        await expect(page.locator('.bowire-ws-detail-title-static')).toHaveText('Workspaces (2)');
        await expect(page.locator('.bowire-env-overview-row')).toHaveCount(2);

        // Exactly one row should carry the active checkmark (the
        // second workspace, since createWorkspace flips
        // activeWorkspaceId on creation).
        await expect(page.locator('.bowire-env-overview-check.is-active')).toHaveCount(1);
    });

    test('switching active workspace via the overview updates the topbar chip', async ({ page }) => {
        // Beta was created last → it's active. Switch to Alpha by
        // clicking its ghosted checkmark.
        await page.locator('.bowire-rail-btn[data-rail-mode-id="workspaces"]').click();
        await page.locator('.bowire-sidebar-mode').getByTitle('Open Workspaces overview').click();

        const ws = await readWorkspaces(page) as Array<{ id: string; name: string }>;
        const alpha = ws.find((w) => w.name === 'Alpha')!;
        const beta = ws.find((w) => w.name === 'Beta')!;
        expect(await readActiveWorkspaceId(page)).toBe(beta.id);

        // Click the non-active row's check — switchWorkspace flips
        // activeWorkspaceId + render() repaints.
        await page.locator(`.bowire-env-overview-row[data-ws-id="${alpha.id}"] .bowire-env-overview-check`).click();
        expect(await readActiveWorkspaceId(page)).toBe(alpha.id);
        await expect(page.locator('.bowire-workspace-chip-name')).toHaveText('Alpha');
    });

    test('rename updates the row + persists to localStorage', async ({ page }) => {
        await page.locator('.bowire-rail-btn[data-rail-mode-id="workspaces"]').click();
        await page.locator('.bowire-sidebar-mode').getByTitle('Open Workspaces overview').click();

        const ws = await readWorkspaces(page) as Array<{ id: string; name: string }>;
        const alpha = ws.find((w) => w.name === 'Alpha')!;
        const alphaRow = page.locator(`.bowire-env-overview-row[data-ws-id="${alpha.id}"]`);
        // Tools cluster is hover-revealed via CSS; for Playwright we
        // hover the row first so the per-row tools become hit-testable.
        await alphaRow.hover();
        // Rename is the 'pencil' tool inside the per-row tools group.
        // Match by aria-label set on the button from the action def's
        // label.
        await alphaRow.locator('.bowire-env-overview-tool[aria-label="Rename workspace"]').click();

        // bowirePrompt overlay opens with the current name pre-filled.
        const promptInput = page.locator('.bowire-prompt-dialog .bowire-prompt-input');
        await expect(promptInput).toBeVisible();
        await promptInput.fill('Alpha Renamed');
        // Confirm (Enter or the non-cancel button).
        await page.locator('.bowire-prompt-dialog .bowire-confirm-btn:not(.cancel)').click();
        await expect(page.locator('.bowire-prompt-dialog')).toHaveCount(0);

        // Row name button now reads the new name.
        await expect(
            page.locator(`.bowire-env-overview-row[data-ws-id="${alpha.id}"] .bowire-env-overview-name`)
        ).toHaveText('Alpha Renamed');

        // localStorage reflects the rename.
        const after = await readWorkspaces(page) as Array<{ id: string; name: string }>;
        expect(after.find((w) => w.id === alpha.id)!.name).toBe('Alpha Renamed');
    });

    test('duplicate adds a third workspace with the chosen name', async ({ page }) => {
        await page.locator('.bowire-rail-btn[data-rail-mode-id="workspaces"]').click();
        await page.locator('.bowire-sidebar-mode').getByTitle('Open Workspaces overview').click();

        const ws = await readWorkspaces(page) as Array<{ id: string; name: string }>;
        const alpha = ws.find((w) => w.name === 'Alpha')!;
        const alphaRow = page.locator(`.bowire-env-overview-row[data-ws-id="${alpha.id}"]`);
        await alphaRow.hover();
        await alphaRow.locator('.bowire-env-overview-tool[aria-label="Duplicate workspace"]').click();

        const promptInput = page.locator('.bowire-prompt-dialog .bowire-prompt-input');
        await expect(promptInput).toBeVisible();
        await promptInput.fill('Alpha Clone');
        await page.locator('.bowire-prompt-dialog .bowire-confirm-btn:not(.cancel)').click();
        await expect(page.locator('.bowire-prompt-dialog')).toHaveCount(0);

        const after = await readWorkspaces(page) as Array<{ name: string }>;
        expect(after.length).toBe(3);
        expect(after.map((w) => w.name).sort()).toEqual(['Alpha', 'Alpha Clone', 'Beta']);
    });

    test('save-as-template makes the workspace available in the create dialog', async ({ page }) => {
        await page.locator('.bowire-rail-btn[data-rail-mode-id="workspaces"]').click();
        await page.locator('.bowire-sidebar-mode').getByTitle('Open Workspaces overview').click();

        const ws = await readWorkspaces(page) as Array<{ id: string; name: string }>;
        const alpha = ws.find((w) => w.name === 'Alpha')!;
        const alphaRow = page.locator(`.bowire-env-overview-row[data-ws-id="${alpha.id}"]`);
        await alphaRow.hover();
        await alphaRow
            .locator('.bowire-env-overview-tool[aria-label*="Save workspace as template"]')
            .click();

        const promptInput = page.locator('.bowire-prompt-dialog .bowire-prompt-input');
        await expect(promptInput).toBeVisible();
        await promptInput.fill('Alpha Template');
        await page.locator('.bowire-prompt-dialog .bowire-confirm-btn:not(.cancel)').click();
        await expect(page.locator('.bowire-prompt-dialog')).toHaveCount(0);

        // saveWorkspaceAsTemplate writes to bowire_user_workspace_templates.
        const userTemplates = await page.evaluate(() => {
            const raw = localStorage.getItem('bowire_user_workspace_templates');
            return raw ? JSON.parse(raw) : [];
        }) as Array<{ name: string }>;
        expect(userTemplates.length).toBe(1);
        expect(userTemplates[0].name).toBe('Alpha Template');

        // Re-open the create-workspace dialog via the workspace chip
        // menu and assert the user template appears in the list.
        await page.locator('#bowire-workspace-chip').click();
        await page
            .locator('.bowire-workspace-menu .bowire-workspace-menu-item-action')
            .filter({ hasText: 'New workspace…' })
            .first()
            .click();
        await expect(page.locator('.bowire-ws-create-dialog')).toBeVisible();
        // User-template rows share the template-row class but carry
        // a delete-button inside; assert at least one row labelled
        // "Alpha Template" is rendered.
        await expect(
            page
                .locator('.bowire-ws-template-row .bowire-ws-template-label')
                .filter({ hasText: 'Alpha Template' })
        ).toHaveCount(1);
    });

    test('deleting the last workspace returns to the empty state and rails stay clickable', async ({ page }) => {
        await page.locator('.bowire-rail-btn[data-rail-mode-id="workspaces"]').click();
        await page.locator('.bowire-sidebar-mode').getByTitle('Open Workspaces overview').click();

        // Delete both workspaces in turn. Each delete fires a
        // bowireConfirm overlay — confirm via the dialog's
        // confirm button. Read workspace ids fresh each loop so a
        // re-render that swaps DOM nodes doesn't trip us up.
        for (let i = 0; i < 2; i++) {
            const ws = await readWorkspaces(page) as Array<{ id: string }>;
            const target = ws[0];
            const row = page.locator(`.bowire-env-overview-row[data-ws-id="${target.id}"]`);
            await row.hover();
            await row.locator('.bowire-env-overview-tool[aria-label="Delete workspace"]').click();
            // Confirm dialog — danger button (still .bowire-confirm-btn).
            await page.locator('.bowire-confirm-dialog .bowire-confirm-btn:not(.cancel)').click();
            await expect(page.locator('.bowire-confirm-dialog')).toHaveCount(0);
        }

        // No workspaces left.
        expect(await readWorkspaces(page)).toEqual([]);
        // The overview's empty branch renders the canonical empty card.
        await expect(page.locator('#bowire-main-workspaces .bowire-empty-card-headline'))
            .toHaveText('No workspaces yet');

        // Force-home rule retired — Discover rail must still be
        // clickable and own the main pane after the last workspace
        // was deleted.
        await page.locator('.bowire-rail-btn[data-rail-mode-id="discover"]').click();
        await expect(page.locator('.bowire-rail-btn[data-rail-mode-id="discover"]'))
            .toHaveClass(/active/);
        const persistedMode = await page.evaluate(() => localStorage.getItem('bowire_rail_mode'));
        expect(persistedMode).toBe('discover');
    });
});
