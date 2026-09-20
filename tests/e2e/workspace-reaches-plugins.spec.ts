import { test, expect, type Page } from '@playwright/test';

/**
 * A per-workspace plugin setting has to reach the plugin (#640).
 *
 * `plugin-settings.spec.ts` already proves a value set in a workspace lands on
 * the server and comes back. That is the write half, and it was green while
 * the feature did nothing: the two calls that actually reach a plugin —
 * `/api/services` and `/api/invoke` — sent no `workspaceId`, so the server
 * entered no workspace scope, `BowirePluginSettingsStore.ResolvePath` returned
 * null, and every setting fell back to its declared default.
 *
 * Measured before the fix, on this exact sample: the same SOAP call answered
 * `soap_version: 1.1` from the workbench and `1.2` when the workspace was
 * named by hand on the query. The settings page wrote 1.2 and nothing read it.
 *
 * So this asserts the read half, and it asserts it on the answer rather than
 * on the control: reading the input back would pass for the bug too, because
 * the browser remembered the value all along.
 *
 * Driven against the workbench on :5191, pointed at the SOAP sample on :5195.
 */

const WORKBENCH = 'http://localhost:5191/';
const SETTING_ID = '#bowire-plugin-setting-soap-defaultSoapVersion';

test.setTimeout(90_000);

/** The invoke answers the page received, newest last. */
function captureInvokes(page: Page): Array<{ url: string; body: Promise<unknown> }> {
    const seen: Array<{ url: string; body: Promise<unknown> }> = [];
    page.on('response', r => {
        if (r.url().includes('/api/invoke')) seen.push({ url: r.url(), body: r.json().catch(() => null) });
    });
    return seen;
}

async function freshWorkspace(page: Page, name: string): Promise<void> {
    await page.goto(WORKBENCH, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });
    await page.evaluate(async () => {
        try {
            const prefix = (window as unknown as { __BOWIRE_CONFIG__?: { prefix?: string } })
                .__BOWIRE_CONFIG__?.prefix ?? '';
            await fetch(prefix + '/api/workspaces', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ workspaces: [] }),
            });
        } catch { /* a host without the endpoint keeps the old behaviour */ }
        try { localStorage.clear(); } catch { /* ignore */ }
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });

    await page.locator('#bowire-welcome-create-btn').click();
    await page.locator('.bowire-ws-create-dialog').waitFor({ timeout: 10_000 });
    await page.locator('.bowire-ws-create-dialog input[type="text"]').first().fill(name);
    await page.locator('.bowire-ws-create-dialog .bowire-confirm-btn:not(.cancel)').click();
    await expect(page.locator('.bowire-ws-create-dialog')).toHaveCount(0, { timeout: 15_000 });
}

async function setSoapVersion(page: Page, value: string): Promise<void> {
    await page.locator('.bowire-rail-settings').click();
    await expect(page.locator('.bowire-settings-overlay')).toBeVisible();
    await page.locator('.bowire-settings-left').getByText('Plugins', { exact: true }).click();

    const input = page.locator(SETTING_ID);
    await expect(input).toBeVisible({ timeout: 15_000 });
    await input.fill(value);
    await input.blur();

    // Written before the overlay closes, so the invoke below cannot race it.
    await expect.poll(async () => {
        const wsId = await page.evaluate(() => {
            try { return (JSON.parse(localStorage.getItem('bowire_workspaces') || '[]') as Array<{ id: string }>)[0]?.id ?? ''; }
            catch { return ''; }
        });
        const body = await page.request
            .get(`${WORKBENCH}api/plugins/settings?workspaceId=${encodeURIComponent(wsId)}`)
            .then(r => r.json());
        return body?.settings?.soap?.defaultSoapVersion ?? null;
    }, { timeout: 10_000 }).toBe(value);

    await page.keyboard.press('Escape');
    await expect(page.locator('.bowire-settings-overlay')).toHaveCount(0, { timeout: 10_000 });
}

async function invokeAdd(page: Page): Promise<void> {
    // The rail button rather than the Home shortcut: the shortcut is part of
    // the welcome card, which is not there once a workspace exists — and not
    // there in the same shape when none does.
    await page.locator('.bowire-rail-btn[data-rail-mode-id="discover"]').click();
    await expect(page.locator('.bowire-method-item').first()).toBeVisible({ timeout: 30_000 });

    await page.locator('.bowire-method-item', { hasText: 'Add' }).first().click();
    const args = page.locator('input.bowire-form-input:visible');
    await expect(args.first()).toBeVisible({ timeout: 15_000 });
    await args.nth(0).fill('2');
    await args.nth(1).fill('3');
    await page.locator('#bowire-action-execute-btn').click();
    await expect(page.locator('.bowire-response-output'))
        .toContainText('<result>5</result>', { timeout: 20_000 });
}

test.describe('A workspace setting reaches the plugin', () => {
    test('the invoke names the workspace it is being made in', async ({ page }) => {
        // The fix itself: without this parameter the server has no workspace
        // to resolve a setting against, whatever the settings page stored.
        const sent: string[] = [];
        page.on('request', r => { if (r.url().includes('/api/invoke')) sent.push(r.url()); });

        await freshWorkspace(page, 'Reaches Plugins');
        await invokeAdd(page);

        expect(sent).toHaveLength(1);
        expect(sent[0]).toContain('workspaceId=');
    });

    test('discovery names it too, so a plugin can read a setting while probing', async ({ page }) => {
        // MQTT's scanDuration is read inside DiscoverAsync; SOAP's is not.
        // The parameter is what both depend on, so it is what gets pinned.
        const sent: string[] = [];
        page.on('request', r => { if (r.url().includes('/api/services')) sent.push(r.url()); });

        await freshWorkspace(page, 'Discovery Names It');
        await page.locator('.bowire-rail-btn[data-rail-mode-id="discover"]').click();
        await expect(page.locator('.bowire-method-item').first()).toBeVisible({ timeout: 30_000 });

        expect(sent.some(u => u.includes('workspaceId='))).toBe(true);
    });

    test('the setting changes what the server actually sends', async ({ page }) => {
        // The effect, not the round-trip. Before the fix this answered 1.1 no
        // matter what the workspace said.
        const answers = captureInvokes(page);

        await freshWorkspace(page, 'Soap Version');
        await setSoapVersion(page, '1.2');
        await invokeAdd(page);

        expect(answers).toHaveLength(1);
        const body = await answers[0].body as { metadata?: Record<string, string> };
        expect(body?.metadata?.soap_version).toBe('1.2');
    });

    test('without a workspace the plugin keeps its declared default', async ({ page }) => {
        // The other half of the same rule: no workspace named, no override —
        // and a workbench with no workspace still has to work.
        const answers = captureInvokes(page);

        await page.goto(WORKBENCH, { waitUntil: 'domcontentloaded' });
        await page.waitForSelector('#bowire-app', { timeout: 30_000 });
        await page.evaluate(async () => {
            try {
                await fetch('/api/workspaces', {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ workspaces: [] }),
                });
            } catch { /* ignore */ }
            try { localStorage.clear(); } catch { /* ignore */ }
        });
        await page.reload({ waitUntil: 'domcontentloaded' });
        await page.waitForSelector('#bowire-app', { timeout: 30_000 });

        await invokeAdd(page);

        const body = await answers[0].body as { metadata?: Record<string, string> };
        expect(body?.metadata?.soap_version).toBe('1.1');
    });
});
