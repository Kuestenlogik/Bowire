import { test, expect, type Page } from '@playwright/test';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

/**
 * The schema drop zone, driven through its own control (#733).
 *
 * `schema-upload.spec.ts` asserts what happens *after* an upload, but posts it
 * the way the drop zone would rather than clicking the drop zone — the control
 * was unreachable from a test. It lives in a workspace's Sources detail,
 * behind a tree whose rows carried no identity in the DOM, so the only way in
 * was a click path through translated labels that would break on a wording
 * change rather than on the behaviour under test.
 *
 * `renderTree` now writes each node's own id into `data-tree-node`, which is
 * what makes this spec possible. Every tree in the workbench gets the same
 * handle.
 *
 * What is covered here and nowhere else: the chooser accepts the file, the
 * name travels, and the message the operator reads afterwards says what the
 * server actually did. That last one is the part `714140d9` fixed — before it,
 * a `.proto` with a syntax error was announced as imported, in green.
 */

const WORKBENCH = 'http://localhost:5191/';

const GOOD_PROTO = `
syntax = "proto3";
package chooser;
service Beacon {
  rpc Ping (PingRequest) returns (PingReply);
}
message PingRequest { string id = 1; }
message PingReply { string status = 1; }
`;

/** Three dot-separated segments of nothing: parses to no service at all. */
const BROKEN_PROTO = 'syntax = "proto3";\nservice { rpc ( ) returns ( ); }\n';

test.setTimeout(90_000);

function onDisk(name: string, content: string): string {
    const dir = mkdtempSync(join(tmpdir(), 'bowire-e2e-dz-'));
    const path = join(dir, name);
    writeFileSync(path, content, 'utf8');
    return path;
}

/** A fresh workspace, then its Sources detail, where the drop zone lives. */
async function openDropZone(page: Page): Promise<void> {
    await page.goto(WORKBENCH, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });

    await page.evaluate(async () => {
        await fetch('/api/workspaces', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ workspaces: [] }),
        }).catch(() => { });
        await fetch('/api/proto/upload', { method: 'DELETE' }).catch(() => { });
        await fetch('/api/openapi/upload', { method: 'DELETE' }).catch(() => { });
        try { localStorage.clear(); } catch { /* ignore */ }
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });

    await page.locator('#bowire-welcome-create-btn').click();
    await page.locator('.bowire-ws-create-dialog').waitFor({ timeout: 10_000 });
    await page.locator('.bowire-ws-create-dialog input[type="text"]').first().fill('Dropzone');
    await page.locator('.bowire-ws-create-dialog .bowire-confirm-btn:not(.cancel)').click();
    await expect(page.locator('.bowire-ws-create-dialog')).toHaveCount(0, { timeout: 15_000 });

    // The rail re-renders when a workspace appears, so the button exists
    // before it is clickable.
    const workspaces = page.locator('.bowire-rail-btn[data-rail-mode-id="workspaces"]');
    await expect(workspaces).toBeVisible({ timeout: 20_000 });
    await workspaces.click();

    const sources = page.locator('.bowire-tree-row[data-tree-node$=":sources"]').first();
    await expect(sources).toBeVisible({ timeout: 20_000 });
    await sources.click();

    await expect(page.locator('#bowire-proto-dropzone')).toBeVisible({ timeout: 20_000 });
}

/** Click the drop zone and hand its chooser a file. */
async function drop(page: Page, path: string): Promise<void> {
    const chooser = page.waitForEvent('filechooser', { timeout: 15_000 });
    await page.locator('#bowire-proto-dropzone').click();
    await (await chooser).setFiles(path);
}

test.describe('The schema drop zone', () => {
    test('a dropped .proto reaches the server under its own name', async ({ page }) => {
        // The name is what makes a git-native workspace carry a reviewable
        // .proto rather than a JSON string with escaped newlines (#654).
        const posted: string[] = [];
        page.on('request', r => {
            // POST only: openDropZone clears the store first, and a DELETE to
            // the same route would be counted as an upload.
            if (r.method() === 'POST' && r.url().includes('/api/proto/upload')) posted.push(r.url());
        });

        await openDropZone(page);
        await drop(page, onDisk('beacon.proto', GOOD_PROTO));

        await expect.poll(async () => posted.length, { timeout: 20_000 }).toBe(1);
        expect(posted[0]).toContain('name=beacon.proto');

        // Asked with the workspace, because the drop zone uploads with it
        // (#640): the schema lands in the workspace's own directory, and a
        // request that names none looks somewhere else entirely. That the
        // two agree is the thing worth asserting here.
        await expect.poll(async () => {
            return await page.evaluate(async () => {
                const wsId = (() => {
                    try {
                        return (JSON.parse(localStorage.getItem('bowire_workspaces') || '[]') as Array<{ id: string }>)[0]?.id ?? '';
                    } catch { return ''; }
                })();
                const query = wsId ? `?workspaceId=${encodeURIComponent(wsId)}` : '';
                const body = await (await fetch('/api/services' + query)).json();
                const services = Array.isArray(body) ? body : body.services ?? [];
                return services.some((s: { name: string }) => s.name === 'chooser.Beacon');
            });
        }, { timeout: 20_000 }).toBe(true);
    });

    test('the chooser takes more than one file', async ({ page }) => {
        // Dropping a service's .proto next to its OpenAPI document in one
        // go is the ordinary case, and a chooser that lost `multiple` would
        // silently take only the first.
        await openDropZone(page);

        const chooser = page.waitForEvent('filechooser', { timeout: 15_000 });
        await page.locator('#bowire-proto-dropzone').click();
        const picked = await chooser;

        // Several files at once is what the drop zone promises, and it is what
        // the chooser can be asked about. Its `accept` list cannot: the input
        // is built with createElement and clicked without ever entering the
        // document, so there is no element on the page to read it from and the
        // file-chooser event does not carry it. Asserting the accept list
        // belongs to the unit tests over the fragment, not here.
        expect(picked.isMultiple()).toBe(true);
        await picked.setFiles([]);
    });

    test('a file the server could not read is not announced as imported', async ({ page }) => {
        // The lie `714140d9` removed: the drop zone counted the files it had
        // posted and reported them in green without reading one response, so
        // a .proto with a syntax error said "1 .proto imported".
        await openDropZone(page);
        await drop(page, onDisk('broken.proto', BROKEN_PROTO));

        const toast = page.locator('.bowire-toast').last();
        await expect(toast).toBeVisible({ timeout: 20_000 });
        await expect(toast).not.toContainText('imported', { timeout: 5_000 });
        await expect(toast).not.toContainText('eingelesen', { timeout: 5_000 });
    });

    test('a schema that did land says how many services it brought', async ({ page }) => {
        // The other half of the same message: a real import reports what the
        // server found, not how many files the browser sent.
        await openDropZone(page);
        await drop(page, onDisk('beacon.proto', GOOD_PROTO));

        const toast = page.locator('.bowire-toast').last();
        await expect(toast).toBeVisible({ timeout: 20_000 });
        await expect(toast).toContainText('1', { timeout: 5_000 });
    });
});
