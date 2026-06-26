import type { Page } from '@playwright/test';

/**
 * Shared Playwright helpers for Bowire E2E specs.
 *
 * Every spec needs to: navigate to the Tool, wipe all client storage
 * (localStorage / sessionStorage / IndexedDB so no previous run's
 * workspace seed leaks in), reload, and wait for the app shell to
 * report it's hydrated. The wipe pattern mirrors
 * scripts/capture-first-run.js — same set of storage layers, same
 * theme-pre-seed so the first paint doesn't flash light.
 */
export async function bootFresh(page: Page, theme: 'dark' | 'light' = 'dark'): Promise<void> {
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await page.evaluate(async (t) => {
        try { localStorage.clear(); } catch { /* ignore */ }
        try { sessionStorage.clear(); } catch { /* ignore */ }
        try {
            if (window.indexedDB && (indexedDB as any).databases) {
                const dbs = await (indexedDB as any).databases();
                await Promise.all(dbs.map((d: { name: string }) => new Promise<void>((res) => {
                    const req = indexedDB.deleteDatabase(d.name);
                    req.onsuccess = req.onerror = (req as any).onblocked = () => res();
                })));
            }
        } catch { /* ignore */ }
        try { localStorage.setItem('bowire_theme_pref', t); } catch { /* ignore */ }
    }, theme);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20_000 });
}

/**
 * Read the parsed `bowire_workspaces` array out of localStorage.
 * Returns [] when the key is unset (first run).
 */
export async function readWorkspaces(page: Page): Promise<Array<Record<string, unknown>>> {
    return await page.evaluate(() => {
        try {
            const raw = localStorage.getItem('bowire_workspaces');
            return raw ? JSON.parse(raw) : [];
        } catch {
            return [];
        }
    });
}

/**
 * Read the active workspace id (or null if none).
 */
export async function readActiveWorkspaceId(page: Page): Promise<string | null> {
    return await page.evaluate(() => {
        try { return localStorage.getItem('bowire_active_workspace'); }
        catch { return null; }
    });
}

/**
 * Read a workspace-scoped localStorage value (under bowire_ws_<id>_<base>).
 */
export async function readWorkspaceKey(page: Page, wsId: string, baseKey: string): Promise<unknown> {
    return await page.evaluate(({ id, key }) => {
        try {
            const raw = localStorage.getItem(`bowire_ws_${id}_${key}`);
            return raw ? JSON.parse(raw) : null;
        } catch {
            return null;
        }
    }, { id: wsId, key: baseKey });
}

/**
 * Walk through the create-workspace dialog. Fills the name input,
 * picks the template radio by id, clicks Create. The REST + multi /
 * mock templates trigger a window.location.reload(); the caller
 * should await the reload-induced bowire-app-ready re-appearance.
 */
export async function createWorkspaceViaDialog(
    page: Page,
    name: string,
    templateId: 'empty' | 'rest' | 'grpc' | 'mock' | 'multi' = 'empty'
): Promise<void> {
    // Dialog opens via either the welcome-card primary button (first
    // run) or the workspaces-overview "New workspace…" CTA. The
    // dialog itself doesn't care which path opened it — just wait for
    // its name input to appear.
    await page.waitForSelector('.bowire-ws-create-dialog .bowire-prompt-input', { timeout: 5_000 });
    const nameInput = page.locator('.bowire-ws-create-dialog .bowire-prompt-input').first();
    await nameInput.fill(name);
    // Template radio. 'empty' is rendered above the list as a
    // 'start from scratch' row; the others land inside the list
    // proper. Both kinds carry data-tpl-id so the same selector works.
    await page.locator(`.bowire-ws-create-dialog [data-tpl-id="${templateId}"]`).click();
    // The Create button is the rightmost confirm-action (Cancel is
    // first, Create is second). Use textContent rather than position
    // so the lookup tolerates layout tweaks.
    await page.locator('.bowire-ws-create-dialog .bowire-confirm-btn:not(.cancel)').click();
}
