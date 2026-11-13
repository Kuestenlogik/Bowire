import { test, expect, type Page } from '@playwright/test';
import { mkdtempSync, writeFileSync, copyFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

/**
 * A gRPC server without reflection, called from the workbench (#991).
 *
 * Reflection off is the recommended production state, and until now it meant
 * the workbench could list such a server's methods from a `.proto` and call
 * none of them: a `.proto` gives names, and marshalling a message needs its
 * compiled descriptors. Those could only arrive through a metadata marker the
 * CLI sets and the workbench has no control for.
 *
 * So the drop zone takes the compiled descriptor set. These drive the real
 * control against the gRPC sample in its `--hardened` shape (h2c on :5183, no
 * reflection service): the file goes out as bytes, the service it describes is
 * listed for that server, and a call through it comes back.
 *
 * `fixtures/greeter.protoset` is the sample's own contract, compiled:
 *
 *   protoc --descriptor_set_out=tests/e2e/fixtures/greeter.protoset --include_imports \
 *          -I samples/Kuestenlogik.Bowire.Sample.Grpc/Protos greeter.proto
 *
 * Regenerate it when that .proto changes.
 */

// The suite's main workbench (:5180), not the one on :5191 the other
// drop-zone specs use: that one is started with --url, so it dials only
// the address it was configured with and refuses this server.
const WORKBENCH = '/';
const HARDENED = 'http://localhost:5183';
const GREETER = 'bowire.samples.greeter.Greeter';
const FIXTURE = join(__dirname, 'fixtures', 'greeter.protoset');

test.setTimeout(90_000);

function scratch(name: string): string {
    return join(mkdtempSync(join(tmpdir(), 'bowire-e2e-ds-')), name);
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
        await fetch('/api/protoset/upload', { method: 'DELETE' }).catch(() => { });
        await fetch('/api/openapi/upload', { method: 'DELETE' }).catch(() => { });
        try { localStorage.clear(); } catch { /* ignore */ }
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });

    await page.locator('#bowire-welcome-create-btn').click();
    await page.locator('.bowire-ws-create-dialog').waitFor({ timeout: 10_000 });
    await page.locator('.bowire-ws-create-dialog input[type="text"]').first().fill('Hardened');
    await page.locator('.bowire-ws-create-dialog .bowire-confirm-btn:not(.cancel)').click();
    await expect(page.locator('.bowire-ws-create-dialog')).toHaveCount(0, { timeout: 15_000 });

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

/**
 * Drop the fixture and return the workspace part of the query it was
 * uploaded under, as `&workspaceId=…`.
 *
 * Taken from the upload itself rather than rebuilt: the drop zone names the
 * active workspace (#640), the set lands in that workspace's directory, and a
 * later request that names another, or none, looks somewhere else. Reading
 * it off the request is the one way to ask about the same place without
 * repeating how the workbench decides it.
 */
async function dropFixture(page: Page): Promise<string> {
    const upload = page.waitForRequest(
        r => r.method() === 'POST' && r.url().includes('/api/protoset/upload'), { timeout: 20_000 });
    await drop(page, FIXTURE);
    const params = new URL((await upload).url()).searchParams;
    params.delete('name');
    await expect(page.locator('.bowire-toast').last()).toBeVisible({ timeout: 20_000 });
    const rest = params.toString();
    return rest ? '&' + rest : '';
}

test.describe('A descriptor set in the drop zone', () => {
    test('it goes out as the bytes it is and says what it described', async ({ page }) => {
        // Read as text the set would be re-encoded on the way out, and what
        // arrived would no longer parse. The server answering with a service
        // count is the proof that it did.
        const posted: string[] = [];
        page.on('request', r => {
            if (r.method() === 'POST' && r.url().includes('/api/protoset/upload')) posted.push(r.url());
        });

        await openDropZone(page);
        await drop(page, FIXTURE);

        const toast = page.locator('.bowire-toast').last();
        await expect(toast).toBeVisible({ timeout: 20_000 });
        await expect(toast).toContainText(/1 (service|Dienst)/, { timeout: 10_000 });
        await expect(toast).toContainText(/descriptor set|Descriptor-Set/);

        expect(posted).toHaveLength(1);
        expect(posted[0]).toContain('name=greeter.protoset');
    });

    test('the server without reflection is listed and answers a call', async ({ page }) => {
        await openDropZone(page);
        const ws = await dropFixture(page);

        const listed = await page.evaluate(async ({ url, ws }) => {
            const body = await (await fetch(
                `/api/services?serverUrl=${encodeURIComponent('grpc@' + url)}${ws}&includeAttempts=1`)).json();
            const list = Array.isArray(body) ? body : body.services ?? [];
            return list.map((s: { name: string; source: string; isUploaded?: boolean; methods: unknown[] }) =>
                ({ name: s.name, source: s.source, uploaded: s.isUploaded === true, methods: s.methods.length }));
        }, { url: HARDENED, ws });

        // `source: grpc` is what routes the call to the gRPC plugin; a
        // .proto upload lists the same name under `proto` and stops there.
        expect(listed).toContainEqual({ name: GREETER, source: 'grpc', uploaded: true, methods: 5 });

        const reply = await page.evaluate(async ({ url, service, ws }) => {
            const res = await fetch(`/api/invoke?serverUrl=${encodeURIComponent(url)}${ws}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    protocol: 'grpc', service, method: 'SayHello',
                    messages: [JSON.stringify({ name: 'no reflection' })],
                }),
            });
            return await res.json();
        }, { url: HARDENED, service: GREETER, ws });

        expect(reply.status).toBe('OK');
        expect(reply.response).toContain('Hello, no reflection!');
    });

    test('a .proto under a descriptor set\'s name is refused with the reason', async ({ page }) => {
        // The mistake this will see most: the source file, renamed or simply
        // mistaken for its compiled form. "Could not import" would leave the
        // operator guessing; the server's sentence names the protoc line.
        const path = scratch('api.protoset');
        writeFileSync(path, 'syntax = "proto3";\nservice Greeter {}\n', 'utf8');

        await openDropZone(page);
        await drop(page, path);

        const toast = page.locator('.bowire-toast').last();
        await expect(toast).toBeVisible({ timeout: 20_000 });
        await expect(toast).toContainText('descriptor_set_out', { timeout: 10_000 });
    });

    test('a .pb is taken as a descriptor set too', async ({ page }) => {
        // What build scripts tend to call the same file.
        const path = scratch('greeter.pb');
        copyFileSync(FIXTURE, path);
        const posted: string[] = [];
        page.on('request', r => {
            if (r.method() === 'POST' && r.url().includes('/upload')) posted.push(new URL(r.url()).pathname);
        });

        await openDropZone(page);
        await drop(page, path);

        await expect(page.locator('.bowire-toast').last()).toContainText(/1 (service|Dienst)/, { timeout: 20_000 });
        expect(posted).toEqual(['/api/protoset/upload']);
    });
});
