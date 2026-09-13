import { test, expect, type Page } from '@playwright/test';
import { bootFresh, createWorkspaceViaDialog, openRail } from './helpers';

/**
 * The environment editor as a source for the shelf (#251), and the focus
 * defect found on the way there (#706).
 *
 * Both need a real browser: #706 is about where focus lands after a DOM
 * move, and the shelf's "Send to shelf" is a right-click on a field that
 * has to be *readable* at that moment — a fragment test can stub the
 * resolver but not the click that was lost before it.
 */
test.use({ locale: 'en-US' });

const ROW = '.bowire-env-editor-row:not(.bowire-env-editor-col-header)';

async function openEnvEditor(page: Page): Promise<void> {
    await page.locator('#bowire-welcome-create-btn').click();
    await createWorkspaceViaDialog(page, 'Shelf Probe', 'empty');
    await expect(page.locator('.bowire-ws-create-dialog')).toHaveCount(0);

    await openRail(page, 'workspaces');
    await page.getByText('Environments', { exact: true }).first().click();
    await page.getByRole('button', { name: 'New environment' }).first().click();
    const prompt = page.locator('.bowire-prompt-input').last();
    await prompt.fill('staging');
    await page.locator('.bowire-confirm-btn:not(.cancel)').last().click();
    await expect(page.locator('.bowire-env-editor-table')).toBeVisible();
}

function row(page: Page, i: number) {
    return page.locator(ROW).nth(i);
}

/** Click into a row's key, type, click into its value, type, then commit. */
async function typeVar(page: Page, i: number, key: string, value: string): Promise<void> {
    await row(page, i).locator('.bowire-env-editor-key').click();
    await page.keyboard.type(key);
    await row(page, i).locator('.bowire-env-editor-val').click();
    await page.keyboard.type(value);
    // Commit by moving focus off the row — the editor persists on change.
    await page.locator('.bowire-env-editor-header').first().click({ force: true });
}

function sendToShelf(page: Page) {
    return page.locator('.bowire-context-menu .bowire-context-menu-item', { hasText: 'Send to shelf' });
}

test.describe('Environment editor → shelf', () => {
    test.beforeEach(async ({ page }) => {
        await bootFresh(page);
        await openEnvEditor(page);
    });

    test('the first click into a never-focused field keeps focus (#706)', async ({ page }) => {
        // The value field has never been focused, so this click is the one
        // that wraps it in the chip overlay — the move that used to blur it.
        const val = row(page, 0).locator('.bowire-env-editor-val');
        await val.click();
        await expect(val).toBeFocused();
        await page.keyboard.type('st-c4f1');
        await expect(val).toHaveValue('st-c4f1');
        // And it did get the overlay — the fix is a re-focus, not a skip.
        await expect(val).toHaveClass(/bowire-vars-chip-field/);
    });

    test('right-click on a variable offers Send to shelf, and the item carries the value', async ({ page }) => {
        await typeVar(page, 0, 'apiKey', 'st-c4f1-d29a-7b00');
        await expect(row(page, 0).locator('.bowire-env-editor-val')).toHaveValue('st-c4f1-d29a-7b00');

        // Either field of the row is the row: the name tells you which
        // value it was, the value is what you paste. Both must resolve.
        await row(page, 0).locator('.bowire-env-editor-key').click({ button: 'right' });
        await expect(sendToShelf(page)).toHaveCount(1);
        await page.keyboard.press('Escape');

        await row(page, 0).locator('.bowire-env-editor-val').click({ button: 'right' });
        await expect(sendToShelf(page)).toHaveCount(1);
        await sendToShelf(page).click();

        await page.locator('#bowire-statusbar-shelf-btn').click();
        const item = page.locator('.bowire-shelf-row').first();
        await expect(item).toBeVisible();
        await expect(item.locator('.bowire-shelf-row-label')).toHaveText('apiKey');
        await expect(item.locator('.bowire-shelf-row-preview')).toHaveText('st-c4f1-d29a-7b00');
        await expect(item).toHaveAttribute('title', 'var:apiKey');
    });

    test('the column header and an empty row are not sources', async ({ page }) => {
        await page.locator('.bowire-env-editor-col-header').first().click({ button: 'right' });
        await expect(page.locator('.bowire-context-menu')).toHaveCount(0);

        // The initial row is empty: nothing to hold, so nothing offered.
        await row(page, 0).locator('.bowire-env-editor-val').click({ button: 'right' });
        await expect(page.locator('.bowire-context-menu')).toHaveCount(0);
    });

    test('a shelved variable goes back into another variable by click', async ({ page }) => {
        await typeVar(page, 0, 'apiKey', 'st-c4f1-d29a-7b00');
        await row(page, 0).locator('.bowire-env-editor-val').click({ button: 'right' });
        await sendToShelf(page).click();

        await page.locator('#bowire-env-add-variable-btn').click();
        await row(page, 1).locator('.bowire-env-editor-key').click();
        await page.keyboard.type('apiKeyCopy');
        const target = row(page, 1).locator('.bowire-env-editor-val');
        await target.click();
        await expect(target).toBeFocused();

        // Open the shelf only now: the drawer lies over the value column
        // at this viewport, and the point is that the shelf remembers the
        // field the operator was in, not the button they opened it with.
        await page.locator('#bowire-statusbar-shelf-btn').click();
        await page.locator('.bowire-shelf-row').first().click();
        await expect(target).toHaveValue('st-c4f1-d29a-7b00');
    });
});
