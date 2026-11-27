import { test, expect } from '@playwright/test';
import { bootFresh, createWorkspaceViaDialog, openRail, readActiveWorkspaceId } from './helpers';

/**
 * #1062 — pre-request and post-response scripts run in the workbench, under
 * its real Content-Security-Policy.
 *
 * Every earlier check of the script runtime missed that no script ran in the
 * page at all: unit tests call the runtime outside a page, and
 * `page.evaluate` is exempt from the page's policy. So nothing here evaluates
 * a script from the test: the collection is stored through the API, and the
 * run is a click. The scripts run in QuickJS (script-sandbox.js), and the
 * page's policy still refuses 'unsafe-eval'.
 *
 * The requests go to this server's own GET /api/project, which answers
 * {"found":false} when no manifest is open.
 */
test.use({ locale: 'en-US' });

test('a collection run executes its items\' scripts under the page\'s policy', async ({ page, baseURL }) => {
    const cspViolations: string[] = [];
    page.on('console', (msg) => {
        if (/Content Security Policy/i.test(msg.text())) cspViolations.push(msg.text());
    });

    await bootFresh(page);
    await page.locator('#bowire-welcome-create-btn').click();
    await createWorkspaceViaDialog(page, 'Scripts', 'empty');
    await expect(page.locator('.bowire-ws-create-dialog')).toHaveCount(0);
    const wsId = await readActiveWorkspaceId(page);
    expect(wsId).toBeTruthy();

    const target = new URL('/api/project', baseURL).toString();
    const item = (id: string, name: string, preScript: string, postScript: string) => ({
        id, name, protocol: 'rest', service: '', method: 'GET', methodType: 'Unary',
        serverUrl: target, urlMode: 'inline', messages: [''], body: '', metadata: {},
        kind: 'request-builder', preScript, postScript,
    });
    const put = await page.request.put(`/api/collections?workspaceId=${encodeURIComponent(wsId!)}`, {
        data: {
            collections: [{
                id: 'col_scripts', name: 'Scripts check', items: [
                    item('ci_1', 'passes',
                        "ctx.request.headers.set('X-Check', '1062'); ctx.request.query.set('probe', 'yes');",
                        "ctx.assert.equal(ctx.response.json().found, false, 'found'); ctx.vars.captured.checked = true;"),
                    item('ci_2', 'post fails', '', "ctx.assert.equal(ctx.response.json().found, true, 'found');"),
                    item('ci_3', 'pre fails', "throw new Error('no signing key');", ''),
                ],
            }],
        },
    });
    expect(put.ok()).toBeTruthy();

    // Reload so the workbench reads the stored collection.
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready');

    const invokes: string[] = [];
    page.on('request', (req) => {
        if (req.url().includes('/api/invoke')) invokes.push(decodeURIComponent(req.url()) + ' ' + (req.postData() || ''));
    });

    await openRail(page, 'workspaces');
    await page.getByText('Scripts check', { exact: true }).first().click();
    await page.getByRole('button', { name: 'Run All' }).click();

    await expect(page.getByText('1 / 3 passed')).toBeVisible({ timeout: 15_000 });
    const badges = page.locator('.bowire-recording-step-status');
    await expect(badges.nth(0)).toHaveText('PASS');
    await expect(badges.nth(1)).toHaveText('FAIL');
    await expect(badges.nth(2)).toHaveText('FAIL');

    // The failing pre-request script stopped its call: two calls for three
    // items, and the first carries what its script added.
    expect(invokes).toHaveLength(2);
    expect(invokes[0]).toContain('probe=yes');
    expect(invokes[0]).toContain('"X-Check":"1062"');

    expect(cspViolations).toEqual([]);
});
