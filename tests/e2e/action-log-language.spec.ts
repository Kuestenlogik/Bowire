import { test, expect, type Page } from '@playwright/test';
import { bootFresh } from './helpers';

/**
 * The Activity log follows the interface language (#689).
 *
 * The rows used to be sentences translated once, when the action happened, and then written to
 * storage. Switching the language left them as they were, and an entry recorded before a
 * translation existed stayed English for good.
 *
 * The unit test beside this one pins the resolution; what only a browser can show is that an
 * entry which has been through `localStorage` — written in one language, read back in another —
 * comes out in the language that is active now. That is the half a stored sentence could never do.
 */

const APP = '#bowire-app.bowire-app-ready';

/** Create a workspace, which is the simplest action that records an undoable entry. */
async function recordOneAction(page: Page, name: string): Promise<void> {
    await page.locator('#bowire-welcome-create-btn').click();
    await page.locator('.bowire-ws-create-dialog').waitFor({ timeout: 10_000 });
    await page.locator('.bowire-ws-create-dialog input[type="text"]').first().fill(name);
    await page.locator('.bowire-ws-create-dialog .bowire-confirm-btn:not(.cancel)').click();
    await expect(page.locator('.bowire-ws-create-dialog')).toHaveCount(0, { timeout: 15_000 });
}

/** What the log holds, read out of storage rather than off the screen. */
async function storedEntries(page: Page): Promise<Array<Record<string, unknown>>> {
    return await page.evaluate(() => {
        try {
            const raw = Object.keys(localStorage)
                .filter(k => k.endsWith('action_log'))
                .map(k => localStorage.getItem(k))
                .find(v => v && v !== '[]');
            return raw ? JSON.parse(raw) : [];
        } catch { return []; }
    });
}

/**
 * Reload in the given language with the Activity drawer open, so the rows the assertion is about
 * are on the page. Both are persisted settings, which is also the honest scenario: the operator
 * had the drawer open, switched the language, and the rows are re-read from storage — the one
 * thing a stored sentence could never survive.
 */
/** The Activity rows as the operator reads them. Fails loudly when there are none — a silent
 *  empty list is how an assertion about rendered text stops asserting anything. */
async function rowText(page: Page): Promise<string> {
    const rows = page.locator('.bowire-activity-title');
    await expect(rows.first()).toBeVisible({ timeout: 15_000 });
    return (await rows.allTextContents()).join(' | ');
}

async function reloadIn(page: Page, locale: string): Promise<void> {
    await page.evaluate((l) => {
        try {
            localStorage.setItem('bowire_locale', l);
            localStorage.setItem('bowire_activity_drawer_open', '1');
            localStorage.setItem('bowire_right_drawer_active_tab', 'activity');
        } catch { /* ignore */ }
    }, locale);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector(APP, { timeout: 20_000 });
}

test.describe('The Activity log and the interface language', () => {
    test('a stored entry reads in the language that is active now', async ({ page }) => {
        await bootFresh(page);
        await recordOneAction(page, 'Harbor');
        // Open the drawer on the English side as well, so both halves of the comparison are read
        // off the same surface.
        await reloadIn(page, 'en');

        // Written in English, and stored as a key rather than as the sentence it just showed.
        const entries = await storedEntries(page);
        expect(entries.length).toBeGreaterThan(0);
        expect(entries[0]).toHaveProperty('titleKey');
        expect(entries[0].titleKey).toBe('actionLog.workspaceCreated');

        // What the row says before the switch.
        const english = await rowText(page);
        expect(english).toContain('Created workspace');
        expect(english).toContain('Harbor');

        // Read back in German. Nothing migrated the entry; the row is resolved as it is painted.
        await reloadIn(page, 'de');
        const german = await rowText(page);
        expect(german).toContain('Arbeitsbereich');
        expect(german).not.toContain('Created workspace');
        // The operator's own word is data and is not translated — a workspace called "Harbor"
        // is called that in every language, and a renamed one still reads under the name it had.
        expect(german).toContain('Harbor');
    });

    test('the stored entry carries no rendered sentence to freeze', async ({ page }) => {
        // The defect in one assertion: if a title string is written to storage, it is already too
        // late — whatever language it was in is the language it stays in, wherever it travels.
        await bootFresh(page);
        await recordOneAction(page, 'Frozen');

        const entries = await storedEntries(page);
        expect(entries.length).toBeGreaterThan(0);
        for (const e of entries) {
            expect(e.title ?? null).toBeNull();
            expect(e.titleParams).toMatchObject({ name: expect.anything() });
        }
    });
});
