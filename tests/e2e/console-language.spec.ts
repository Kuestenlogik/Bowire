import { test, expect, type Page } from '@playwright/test';
import { bootFresh, createWorkspaceViaDialog } from './helpers';

/**
 * The Console follows the interface language (#739).
 *
 * Twenty-two console lines carried the action log's exemption — "the action log stores rendered
 * text, see #689" — while writing to a surface that stores nothing. The reason never applied to
 * them, and because nobody had looked, the console stayed English in every locale.
 *
 * The unit test beside this one pins the resolution and the row's colour. What only a browser can
 * show is the half that makes the console different from the action log: it is an in-memory ring
 * buffer, so there is no reload in this story. The language changes *under* the rows that are
 * already there, and they follow — which a sentence translated at the call site could never do,
 * because by then the entry would hold German or English and nothing else.
 */

const APP = '#bowire-app.bowire-app-ready';

/** The console rows as the operator reads them. Fails loudly when there are none: an assertion
 *  about rendered text that runs against an empty list has stopped asserting anything. */
async function consoleStatuses(page: Page): Promise<string> {
    const rows = page.locator('.bowire-console-status');
    await expect(rows.first()).toBeVisible({ timeout: 15_000 });
    return (await rows.allTextContents()).join(' | ');
}

/** A workspace, which the recordings rail needs before it will record anything. */
async function makeWorkspace(page: Page, name: string): Promise<void> {
    await page.locator('#bowire-welcome-create-btn').click();
    await createWorkspaceViaDialog(page, name);
    await expect(page.locator('.bowire-ws-create-dialog')).toHaveCount(0, { timeout: 15_000 });
}

/**
 * Start a recording — the shortest real action that writes a translatable console line.
 *
 * The rail is selected through its stored mode rather than by clicking the rail button: which
 * buttons are directly clickable depends on the window height (#610), and this spec is about the
 * console, not about the rail strip.
 */
async function startARecording(page: Page): Promise<void> {
    await page.evaluate(() => {
        try { localStorage.setItem('bowire_rail_mode', 'recordings'); } catch { /* ignore */ }
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector(APP, { timeout: 20_000 });
    await page.locator('.bowire-empty-card-action-primary').first().click();
    await page.waitForTimeout(250);
}

/** The status-bar button toggles, so opening an open drawer would close it. */
async function openConsoleDrawer(page: Page): Promise<void> {
    if (await page.locator('.bowire-console-body').count() === 0) {
        await page.locator('#bowire-statusbar-console-btn').click();
    }
    await page.locator('.bowire-console-body').waitFor({ timeout: 10_000 });
}

/** Change the language the way an operator does, in the dialog, without reloading. */
async function switchLanguageTo(page: Page, locale: string): Promise<void> {
    await page.locator('.bowire-rail-settings').click();
    await expect(page.locator('.bowire-settings-overlay')).toBeVisible();
    await page.locator('#bowire-settings-locale-select').selectOption(locale);
    await page.keyboard.press('Escape');
    await expect(page.locator('.bowire-settings-overlay')).toHaveCount(0, { timeout: 10_000 });
    await page.waitForSelector(APP, { timeout: 10_000 });
}

test.describe('The Console and the interface language', () => {
    test('a line already on screen follows a language switch, with no reload', async ({ page }) => {
        await bootFresh(page);
        await makeWorkspace(page, 'Harbor');
        await startARecording(page);
        await openConsoleDrawer(page);

        const english = await consoleStatuses(page);
        expect(english).toContain('Recording started');

        // The switch happens under the row that is already there. Nothing is written down and
        // nothing is read back — the entry never held either sentence.
        await switchLanguageTo(page, 'de');
        await openConsoleDrawer(page);

        const german = await consoleStatuses(page);
        expect(german).toContain('Aufzeichnung gestartet');
        expect(german).not.toContain('Recording started');
    });

    test('the entry holds a key, not the sentence it happens to be showing', async ({ page }) => {
        // The defect in one assertion. A rendered sentence on the entry is already too late:
        // whatever language it was written in is the language that row stays in.
        await bootFresh(page);
        await makeWorkspace(page, 'Frozen');
        await startARecording(page);
        await openConsoleDrawer(page);

        const rows = page.locator('.bowire-console-status');
        await expect(rows.first()).toBeVisible({ timeout: 15_000 });

        // Read off the row rather than out of storage — the console has no storage, which is
        // the whole reason its exemption was borrowed from somewhere it did not fit.
        const text = await rows.first().textContent();
        expect(text?.trim()).not.toBe('');
        expect(text).not.toContain('console.status.');
    });
});
