import { test, expect, type Page } from '@playwright/test';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

/**
 * Schema files of the kinds the plugins import, through the drop zone (#994).
 *
 * The drop zone knew two kinds: `.proto` went to the proto endpoint and
 * everything else to the OpenAPI one. A GraphQL schema in SDL, a WSDL or an
 * AsyncAPI document could not be imported, and the "no plugin answered"
 * message told the operator to upload SDL all the same.
 *
 * Now each plugin declares what it imports, one route takes any file and
 * decides its kind, and the drop zone says what this host reads. These drive
 * the real control: the file goes out under its own name, the plugin that
 * owns the format reads it, and what it describes is listed.
 */

const WORKBENCH = '/';

const HARBOR_SDL = `
type Ship { id: ID! name: String! flag: String }
type Query { ship(id: ID!): Ship ships: [Ship!]! }
type Mutation { renameShip(id: ID!, name: String!): Ship }
`;

const CALCULATOR_WSDL = `
<definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
             xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
             xmlns:tns="http://example.com/calc/wsdl"
             targetNamespace="http://example.com/calc/wsdl">
  <message name="AddIn"><part name="a" type="tns:int"/></message>
  <portType name="Calculator">
    <operation name="Add"><input message="tns:AddIn"/></operation>
  </portType>
  <binding name="CalculatorSoap" type="tns:Calculator">
    <operation name="Add"><soap:operation soapAction="http://example.com/calc/Add"/></operation>
  </binding>
</definitions>
`;

const PEOPLE_EDMX = `
<edmx:Edmx xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx" Version="4.0">
  <edmx:DataServices>
    <Schema xmlns="http://docs.oasis-open.org/odata/ns/edm" Namespace="Demo">
      <EntityType Name="Person">
        <Key><PropertyRef Name="UserName"/></Key>
        <Property Name="UserName" Type="Edm.String" Nullable="false"/>
      </EntityType>
      <EntityContainer Name="DemoContainer">
        <EntitySet Name="People" EntityType="Demo.Person"/>
      </EntityContainer>
    </Schema>
  </edmx:DataServices>
</edmx:Edmx>
`;

const HARBOR_OPENRPC = JSON.stringify({
    openrpc: '1.3.2',
    info: { title: 'Harbor', version: '1.0.0' },
    methods: [{ name: 'ship.get', params: [{ name: 'id', schema: { type: 'integer' } }] }],
});

const LIGHTS_ASYNCAPI = `asyncapi: 3.0.0
info:
  title: Lights
  version: 1.0.0
servers:
  production:
    host: 'broker.local:1883'
    protocol: mqtt
channels:
  lightTurnOn:
    address: 'light/turn/on'
    messages:
      turnOn:
        payload:
          type: object
operations:
  sendTurnOn:
    action: send
    channel:
      $ref: '#/channels/lightTurnOn'
`;

test.setTimeout(90_000);

function onDisk(name: string, content: string): string {
    const dir = mkdtempSync(join(tmpdir(), 'bowire-e2e-kinds-'));
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
        await fetch('/api/schema/upload', { method: 'DELETE' }).catch(() => { });
        try { localStorage.clear(); } catch { /* ignore */ }
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });

    await page.locator('#bowire-welcome-create-btn').click();
    await page.locator('.bowire-ws-create-dialog').waitFor({ timeout: 10_000 });
    await page.locator('.bowire-ws-create-dialog input[type="text"]').first().fill('Imports');
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

/**
 * Click the drop zone, hand its chooser a file, and return the workspace
 * part of the query the file was uploaded under, as `&workspaceId=…`.
 */
async function drop(page: Page, path: string): Promise<string> {
    const upload = page.waitForRequest(
        r => r.method() === 'POST' && r.url().includes('/api/schema/upload'), { timeout: 20_000 });
    const chooser = page.waitForEvent('filechooser', { timeout: 15_000 });
    await page.locator('#bowire-proto-dropzone').click();
    await (await chooser).setFiles(path);

    const params = new URL((await upload).url()).searchParams;
    params.delete('name');
    const rest = params.toString();
    return rest ? '&' + rest : '';
}

test.describe('Schema kinds the plugins import', () => {
    test('the drop zone says what this host reads', async ({ page }) => {
        await openDropZone(page);

        // From /api/schema/kinds, so it follows the loaded plugins rather
        // than a list written into the page.
        const hint = page.locator('#bowire-proto-dropzone .bowire-proto-dropzone-hint');
        await expect(hint).toContainText('GraphQL SDL (.graphql, .graphqls, .gql)', { timeout: 20_000 });
        await expect(hint).toContainText('.protoset');
        await expect(hint).toContainText('WSDL (.wsdl)');
        await expect(hint).toContainText('OData EDMX (.edmx, .csdl)');
        await expect(hint).toContainText('OpenRPC (.json)');
        await expect(hint).toContainText('AsyncAPI (.yaml, .yml, .json)');
        await expect(hint).toContainText('OpenAPI / Swagger');
    });

    test('a GraphQL schema in SDL is read by the GraphQL plugin and listed', async ({ page }) => {
        const posted: string[] = [];
        page.on('request', r => {
            if (r.method() === 'POST' && r.url().includes('/upload')) posted.push(r.url());
        });

        await openDropZone(page);
        const ws = await drop(page, onDisk('harbor.graphql', HARBOR_SDL));

        const toast = page.locator('.bowire-toast').last();
        await expect(toast).toBeVisible({ timeout: 20_000 });
        await expect(toast).toContainText('1 GraphQL SDL', { timeout: 10_000 });
        await expect(toast).toContainText(/2 (service|Dienst)/);

        // One route for every kind; the file's own name travels with it.
        expect(posted).toHaveLength(1);
        expect(new URL(posted[0]).pathname).toBe('/api/schema/upload');
        expect(posted[0]).toContain('name=harbor.graphql');

        const listed = await page.evaluate(async (ws) => {
            const body = await (await fetch(`/api/services?includeAttempts=1${ws}`)).json();
            const list = Array.isArray(body) ? body : body.services ?? [];
            return list
                .filter((s: { source: string }) => s.source === 'graphql')
                .map((s: { name: string; isUploaded?: boolean; methods: { name: string }[] }) =>
                    ({ name: s.name, uploaded: s.isUploaded === true, methods: s.methods.map(m => m.name) }));
        }, ws);

        // `source: graphql` is what routes a call to the GraphQL plugin.
        expect(listed).toContainEqual({ name: 'Query', uploaded: true, methods: ['ship', 'ships'] });
        expect(listed).toContainEqual({ name: 'Mutation', uploaded: true, methods: ['renameShip'] });
    });

    test('WSDL, EDMX, OpenRPC and AsyncAPI each reach the plugin that reads them', async ({ page }) => {
        // `.json` and `.yaml` are shared with OpenAPI, so for those two the
        // server has to look inside the file; the toast names what it found.
        const files: { name: string; content: string; label: string; plugin: string; service: string }[] = [
            { name: 'calculator.wsdl', content: CALCULATOR_WSDL, label: 'WSDL', plugin: 'soap', service: 'Calculator' },
            { name: 'people.edmx', content: PEOPLE_EDMX, label: 'OData EDMX', plugin: 'odata', service: 'People' },
            { name: 'harbor.json', content: HARBOR_OPENRPC, label: 'OpenRPC', plugin: 'jsonrpc', service: 'Methods' },
            { name: 'lights.yaml', content: LIGHTS_ASYNCAPI, label: 'AsyncAPI', plugin: 'asyncapi', service: 'lightTurnOn' },
        ];

        await openDropZone(page);
        let ws = '';
        for (const file of files) {
            ws = await drop(page, onDisk(file.name, file.content));
            const toast = page.locator('.bowire-toast').last();
            await expect(toast).toContainText('1 ' + file.label, { timeout: 20_000 });
            await expect(toast).toContainText(/1 (service|Dienst)/);
        }

        const listed = await page.evaluate(async (ws) => {
            const body = await (await fetch(`/api/services?includeAttempts=1${ws}`)).json();
            const list = Array.isArray(body) ? body : body.services ?? [];
            return list
                .filter((s: { isUploaded?: boolean }) => s.isUploaded === true)
                .map((s: { name: string; source: string }) => s.source + ':' + s.name);
        }, ws);

        for (const file of files) expect(listed).toContain(file.plugin + ':' + file.service);
    });

    test('an OpenAPI document under the shared extensions is still stored as OpenAPI', async ({ page }) => {
        await openDropZone(page);
        await drop(page, onDisk('petstore.yaml',
            'openapi: 3.0.0\ninfo:\n  title: Petstore\n  version: "1"\npaths: {}\n'));

        await expect(page.locator('.bowire-toast').last()).toContainText('1 OpenAPI', { timeout: 20_000 });
    });

    test('SDL that does not parse is refused with the parser\'s reason', async ({ page }) => {
        await openDropZone(page);
        await drop(page, onDisk('broken.graphql', 'type Query { ship(: Ship }'));

        const toast = page.locator('.bowire-toast').last();
        await expect(toast).toBeVisible({ timeout: 20_000 });
        await expect(toast).toContainText('Not GraphQL SDL', { timeout: 10_000 });
        await expect(toast).toContainText('broken.graphql');
    });

    test('SDL saved under another extension still finds its reader', async ({ page }) => {
        // The picker filters by extension, but a file can be dragged in under
        // any name, and "schema.txt" is what people save a pasted schema as.
        await openDropZone(page);
        await page.evaluate(async (sdl) => {
            // Posted the way the drop zone posts, without the picker's filter.
            await fetch('/api/schema/upload?name=schema.txt', { method: 'POST', body: sdl })
                .then(async r => { (window as unknown as { __kind: string }).__kind = (await r.json()).kind; });
        }, HARBOR_SDL);

        const kind = await page.evaluate(() => (window as unknown as { __kind: string }).__kind);
        expect(kind).toBe('graphql-sdl');
    });

    test('a file no plugin reads is refused with the list of what can be uploaded', async ({ page }) => {
        await openDropZone(page);
        const refusal = await page.evaluate(async () => {
            const res = await fetch('/api/schema/upload?name=notes.txt', { method: 'POST', body: 'just notes' });
            return { status: res.status, detail: (await res.json()).detail as string };
        });

        expect(refusal.status).toBe(400);
        expect(refusal.detail).toContain('notes.txt');
        expect(refusal.detail).toContain('GraphQL SDL');
        expect(refusal.detail).toContain('.proto');
    });
});
