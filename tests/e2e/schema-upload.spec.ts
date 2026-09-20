import { test, expect, type Page } from '@playwright/test';

/**
 * An uploaded `.proto`, seen from the browser (#654).
 *
 * The upload path had no browser test, which is how it stayed unnoticed that
 * the workbench was the *only* surface showing an uploaded schema:
 * `bowire discover` found nothing and the `bowire.discover` MCP tool answered
 * an agent `services: []` for the same URL. Folding that merge into
 * `BowireDiscoveryProbe` fixed the other two, and moving a merge is exactly
 * the change that can quietly break the surface which already worked, or make
 * it list everything twice — so the merged list is what these assert, read
 * back from the page.
 *
 * One real bug came out of writing this. The first version asserted on the
 * Explore sidebar and flaked: the same sequence against the same server
 * listed the uploaded schema sometimes and not others.
 * `ProtoUploadStore.GetServices()` hands out its parse cache, the merge
 * stamped the origin URL onto those shared objects, and the workbench asks
 * twice per load — once without a `serverUrl`. Whichever landed first decided
 * the origin for every request after it, and when that was the URL-less one
 * the service was pinned to `""` for good, because `??=` does not overwrite
 * an empty string. OriginUrl is what routes an invocation, so the same pin
 * also sends a call to the wrong host in a multi-URL setup. That is fixed by
 * copying before annotating, and pinned by
 * `BowireDiscoveryProbeTests.RunAsync_Gives_Each_Call_Its_Own_Origin_Url`.
 *
 * Two things are still not asserted here, and both are gaps rather than
 * oversights:
 *
 * - The drop zone's file chooser. That control lives in the workspace's
 *   Sources detail, several clicks deep behind a drawer; pinning that
 *   navigation would make this spec fail on a layout change rather than on
 *   the behaviour it is about. The upload below is the same POST it makes.
 * - The sidebar itself. Driven directly it now renders the uploaded methods
 *   on five runs out of five; driven through this harness it did not, and the
 *   difference is not yet understood. A test that passes for a reason nobody
 *   can name is worth less than an admitted gap.
 *
 * Driven against the workbench on :5191, pointed at the SOAP sample on :5195.
 */

const WORKBENCH = 'http://localhost:5191/';

const PROTO = `
syntax = "proto3";
package dropzone;
service Beacon {
  rpc Ping (PingRequest) returns (PingReply);
  rpc Sweep (PingRequest) returns (stream PingReply);
}
message PingRequest { string id = 1; }
message PingReply { string status = 1; }
`;

const DISCOVERED = 'soap%40http%3A%2F%2Flocalhost%3A5195%2FCalculator.asmx%3Fwsdl';

test.setTimeout(90_000);

/** The service names the page is served for a query. */
async function servicesFor(page: Page, query: string): Promise<string[]> {
    return await page.evaluate(async (q) => {
        const body = await (await fetch('/api/services' + q)).json();
        const list = Array.isArray(body) ? body : body.services ?? [];
        return list.map((s: { name: string }) => s.name);
    }, query);
}

async function openWorkbench(page: Page): Promise<void> {
    await page.goto(WORKBENCH, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });

    // No workspace, and nothing uploaded. Both matter: this suite shares a
    // server, so a leftover schema would make the assertions below pass for
    // the wrong reason — and a workspace left behind by another spec would
    // make them fail for an interesting one. Discovery names the active
    // workspace now (#640) while the plain POST below names none, so the two
    // would otherwise resolve to different directories.
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
}

/** The POST the drop zone makes, name and all, then a reload it survives. */
async function upload(page: Page): Promise<void> {
    await page.evaluate(async (body) => {
        await fetch('/api/proto/upload?name=beacon.proto', { method: 'POST', body });
    }, PROTO);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });
}

test.describe('An uploaded schema, from the browser', () => {
    test('it is there after a reload, under the name that was sent', async ({ page }) => {
        // The first of the four things #654 was about, and the name is what
        // makes a git-native workspace carry a reviewable .proto rather than
        // a JSON string with escaped newlines.
        await openWorkbench(page);
        await upload(page);

        expect(await servicesFor(page, '')).toContain('dropzone.Beacon');
    });

    test('it rides alongside what the server described itself', async ({ page }) => {
        // The merge, read back from the page: the SOAP sample's Calculator
        // comes from a live WSDL, the Beacon from a file, and one request
        // returns both.
        await openWorkbench(page);
        await upload(page);

        const names = await servicesFor(page, `?serverUrl=${DISCOVERED}&includeAttempts=1`);
        expect(names).toContain('dropzone.Beacon');
        expect(names).toContain('Calculator');
    });

    test('it appears once, not twice', async ({ page }) => {
        // The failure mode of moving a merge rather than copying it: a
        // surface that still did its own would list every uploaded service
        // twice, and a duplicate reads as a discovery quirk, not a bug.
        await openWorkbench(page);
        await upload(page);

        const names = await servicesFor(page, `?serverUrl=${DISCOVERED}&includeAttempts=1`);
        expect(names.filter(n => n === 'dropzone.Beacon')).toHaveLength(1);
    });

    test('clearing takes it away again', async ({ page }) => {
        await openWorkbench(page);
        await upload(page);
        expect(await servicesFor(page, '')).toContain('dropzone.Beacon');

        await page.evaluate(async () => {
            await fetch('/api/proto/upload', { method: 'DELETE' });
        });

        expect(await servicesFor(page, `?serverUrl=${DISCOVERED}`)).not.toContain('dropzone.Beacon');
    });
});
