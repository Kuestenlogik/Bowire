// What the workbench says after a schema upload.
//
// The drop zone counted the files it *posted* and announced them as imported,
// in green, without reading a single response. So a `.proto` with a syntax
// error said "1 .proto imported" and imported nothing — the server answers
// 200 with `{"imported":0}` and the browser threw that away. An empty file,
// which the endpoint refuses with 400, said the same. So did a server error.
// The one case an operator most needs to hear about was the one dressed up as
// success.
//
// Three call sites did this, character for character. The summary is one
// function now, and these pin the sentence it produces for every shape of
// outcome — that sentence is the whole point of the fix.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const load = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/helpers.js',
    ['config', 't'],
    '\nreturn { summariseSchemaUpload, uploadSchemaFiles, loadSchemaImportKinds, schemaUploadAccept, schemaUploadHint };\n',
);

/** `t` returns the key plus its arguments, so assertions read the intent. */
function harness() {
    return load({
        config: { prefix: '' },
        t: (key, args) => key + (args ? ' ' + JSON.stringify(args) : ''),
    });
}

const proto = (name, imported, ok = true, status = 200) =>
    ({ name, kind: 'proto', ok, status, imported });
const openapi = (name, ok = true, status = 200) =>
    ({ name, kind: 'openapi', ok, status, imported: null });

test('a proto that described services is reported with the count', () => {
    const { summariseSchemaUpload } = harness();
    const r = summariseSchemaUpload([proto('routeguide.proto', 2)]);

    assert.equal(r.level, 'success');
    assert.match(r.text, /sidebar\.upload\.imported/);
    assert.match(r.text, /"services":"2"/);
    assert.match(r.text, /1 \.proto/);
});

test('a proto the server read but found nothing in is not a success', () => {
    // The case the old toast lied about. It is not an error either — the file
    // arrived and is stored — but calling it an import is the same lie in a
    // quieter voice.
    const { summariseSchemaUpload } = harness();
    const r = summariseSchemaUpload([proto('broken.proto', 0)]);

    assert.equal(r.level, 'warning');
    assert.match(r.text, /sidebar\.upload\.noServices/);
    assert.match(r.text, /broken\.proto/);
});

test('a refused upload is an error that names the file', () => {
    // An empty .proto is refused with 400. The operator dropped a file and
    // has to learn which one did not make it.
    const { summariseSchemaUpload } = harness();
    const r = summariseSchemaUpload([proto('empty.proto', null, false, 400)]);

    assert.equal(r.level, 'error');
    assert.match(r.text, /sidebar\.upload\.failed/);
    assert.match(r.text, /empty\.proto/);
});

test('one failure among several is still reported as a failure', () => {
    // The batch case, and the one a count-the-files summary gets most wrong:
    // two of three worked, so the old code said "3 .proto imported".
    const { summariseSchemaUpload } = harness();
    const r = summariseSchemaUpload([
        proto('good.proto', 1),
        proto('bad.proto', null, false, 400),
        proto('also-good.proto', 2),
    ]);

    assert.equal(r.level, 'error');
    assert.match(r.text, /bad\.proto/);
    assert.doesNotMatch(r.text, /good\.proto/);
});

test('an OpenAPI document counts as imported without a service count', () => {
    // OpenAPI is stored raw and parsed later during discovery, so there is no
    // number to report at upload time and none is invented.
    const { summariseSchemaUpload } = harness();
    const r = summariseSchemaUpload([openapi('petstore.yaml')]);

    assert.equal(r.level, 'success');
    assert.match(r.text, /1 OpenAPI/);
    assert.match(r.text, /"services":"0"/);
});

test('a mixed batch names both kinds', () => {
    const { summariseSchemaUpload } = harness();
    const r = summariseSchemaUpload([proto('a.proto', 1), openapi('b.json')]);

    assert.equal(r.level, 'success');
    assert.match(r.text, /1 \.proto \+ 1 OpenAPI/);
});

test('an empty proto beside a good OpenAPI is not downgraded to a warning', () => {
    // The warning is for "you dropped protos and none described anything".
    // With something else in the batch that did land, the summary stays a
    // success rather than implying the whole drop failed.
    const { summariseSchemaUpload } = harness();
    const r = summariseSchemaUpload([proto('empty.proto', 0), openapi('b.json')]);

    assert.equal(r.level, 'success');
});

test('nothing dropped says nothing', () => {
    const { summariseSchemaUpload } = harness();
    assert.equal(summariseSchemaUpload([]).text, '');
    assert.equal(summariseSchemaUpload(null).text, '');
});

// ---- the posting half ----

/** A dropped file: the upload reads every one as bytes. */
const dropped = (name, bytes = new ArrayBuffer(1)) => ({
    name,
    arrayBuffer: () => Promise.resolve(bytes),
    text: () => Promise.reject(new Error('an upload must not be read as text')),
});

test('every file goes to the one upload route, and the server says what it is', async () => {
    // #994 - the browser used to pick the endpoint from the extension, which
    // only worked while there were three kinds and each had its own
    // extensions. `.json` is OpenAPI, AsyncAPI and OpenRPC.
    const { uploadSchemaFiles } = harness();
    const seen = [];
    const kinds = ['proto', 'asyncapi', 'graphql-sdl'];
    const post = (url) => {
        seen.push(url);
        const kind = kinds[seen.length - 1];
        return Promise.resolve({
            ok: true, status: 200,
            json: () => Promise.resolve({ kind, displayName: kind.toUpperCase(), imported: 1 }),
        });
    };

    const results = await uploadSchemaFiles(
        [dropped('a.proto'), dropped('b.yaml'), dropped('c.GraphQL')], post);

    assert.match(seen[0], /\/api\/schema\/upload\?name=a\.proto/);
    assert.match(seen[1], /\/api\/schema\/upload\?name=b\.yaml/);
    assert.match(seen[2], /\/api\/schema\/upload\?name=c\.GraphQL/);
    assert.deepEqual(results.map(r => r.kind), kinds);
    assert.equal(results[2].displayName, 'GRAPHQL-SDL');
});

test('the service count comes back from the server, not from the file list', () => {
    // The shape of the bug: the old code knew only how many files it had sent.
    const { uploadSchemaFiles } = harness();
    return uploadSchemaFiles(
        [dropped('a.proto')],
        () => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ imported: 7 }) }),
    ).then(results => {
        assert.equal(results[0].imported, 7);
        assert.equal(results[0].ok, true);
    });
});

test('a rejected upload is recorded as such', async () => {
    const { uploadSchemaFiles } = harness();
    const results = await uploadSchemaFiles(
        [dropped('empty.proto')],
        () => Promise.resolve({ ok: false, status: 400, json: () => Promise.resolve({}) }),
    );

    assert.equal(results[0].ok, false);
    assert.equal(results[0].status, 400);
});

test('a network failure is a failure, not a silent success', async () => {
    const { uploadSchemaFiles } = harness();
    const results = await uploadSchemaFiles(
        [dropped('a.proto')],
        () => Promise.reject(new Error('offline')),
    );

    assert.equal(results[0].ok, false);
});

// ---- descriptor sets (#991) ----

const protoset = (name, imported, ok = true, status = 200, reason = null) =>
    ({ name, kind: 'protoset', ok, status, imported, reason });

test('a descriptor set is sent as the bytes it was dropped as', async () => {
    // Read as text, a binary set is re-encoded on the way out and arrives as
    // something that no longer parses.
    const { uploadSchemaFiles } = harness();
    const sent = [];
    const bytes = new Uint8Array([0x0a, 0x00, 0xff]).buffer;

    const results = await uploadSchemaFiles(
        [dropped('api.protoset', bytes), dropped('api.PB', bytes), dropped('api.desc', bytes), dropped('api.binpb', bytes)],
        (url, body) => {
            sent.push({ url, body });
            return Promise.resolve({
                ok: true, status: 200,
                json: () => Promise.resolve({ kind: 'protoset', displayName: 'gRPC descriptor set', imported: 2 }),
            });
        });

    for (const s of sent) {
        assert.match(s.url, /\/api\/schema\/upload\?name=api\./);
        assert.equal(s.body, bytes);
    }
    assert.equal(sent.length, 4);
    assert.equal(results[0].kind, 'protoset');
    assert.equal(results[0].imported, 2);
});

test('a descriptor set is reported as one, with its service count', () => {
    const { summariseSchemaUpload } = harness();
    const r = summariseSchemaUpload([protoset('api.protoset', 3)]);

    assert.equal(r.level, 'success');
    assert.match(r.text, /sidebar\.upload\.descriptorSets/);
    assert.match(r.text, /"services":"3"/);
    assert.doesNotMatch(r.text, /protoNamesOnly/);
});

test('a .proto import says it gives names only and points at the descriptor set', () => {
    // The import succeeded, so nothing else would tell the operator before
    // the first call fails.
    const { summariseSchemaUpload } = harness();
    const r = summariseSchemaUpload([proto('routeguide.proto', 2)]);

    assert.equal(r.level, 'success');
    assert.match(r.text, /sidebar\.upload\.protoNamesOnly/);
});

test('a refused descriptor set is reported with the reason the server gave', async () => {
    const { uploadSchemaFiles, summariseSchemaUpload } = harness();
    const results = await uploadSchemaFiles(
        [dropped('api.protoset')],
        () => Promise.resolve({
            ok: false, status: 400,
            json: () => Promise.resolve({ detail: 'The descriptor set lacks shop/money.proto.' }),
        }));

    const r = summariseSchemaUpload(results);
    assert.equal(r.level, 'error');
    assert.match(r.text, /sidebar\.upload\.failedWhy/);
    assert.match(r.text, /shop\/money\.proto/);
});

// ---- kinds the plugins declare (#994) ----

const imported = (name, kind, displayName, count) =>
    ({ name, kind, displayName, ok: true, status: 200, imported: count });

test('a kind a plugin imports is named by the format it is', () => {
    const { summariseSchemaUpload } = harness();
    const r = summariseSchemaUpload([
        imported('harbor.graphql', 'graphql-sdl', 'GraphQL SDL', 2),
        imported('orders.wsdl', 'wsdl', 'WSDL', 1),
        imported('more.graphql', 'graphql-sdl', 'GraphQL SDL', 1),
    ]);

    assert.equal(r.level, 'success');
    assert.match(r.text, /2 GraphQL SDL \+ 1 WSDL/);
    assert.match(r.text, /"services":"4"/);
    assert.doesNotMatch(r.text, /protoNamesOnly/);
});

test('a plugin kind that describes no service is a warning, as for a proto', () => {
    const { summariseSchemaUpload } = harness();
    const r = summariseSchemaUpload([imported('types-only.graphql', 'graphql-sdl', 'GraphQL SDL', 0)]);

    assert.equal(r.level, 'warning');
    assert.match(r.text, /types-only\.graphql/);
});

test('before the host has answered, the picker offers what every host reads', () => {
    const { schemaUploadAccept } = harness();
    assert.equal(schemaUploadAccept(), '.proto');
});

test('the picker and the hint follow what the host says it imports', async () => {
    const h = harness();
    const asked = [];
    await h.loadSchemaImportKinds((url) => {
        asked.push(url);
        return Promise.resolve({
            ok: true,
            json: () => Promise.resolve({
                kinds: [
                    { kind: 'proto', displayName: 'Protobuf source (names only)', extensions: ['.proto'], plugin: 'proto' },
                    { kind: 'graphql-sdl', displayName: 'GraphQL SDL', extensions: ['.graphql', '.gql'], plugin: 'graphql' },
                    { kind: 'asyncapi', displayName: 'AsyncAPI', extensions: ['.json', '.yaml'], plugin: 'asyncapi' },
                    { kind: 'openapi', displayName: 'OpenAPI / Swagger', extensions: ['.json', '.yaml', '.yml'], plugin: 'rest' },
                ],
            }),
        });
    });

    assert.match(asked[0], /\/api\/schema\/kinds$/);
    // Each extension once, however many kinds arrive under it.
    assert.equal(h.schemaUploadAccept(), '.proto,.graphql,.gql,.json,.yaml,.yml');
    const hint = h.schemaUploadHint();
    assert.match(hint, /GraphQL SDL \(\.graphql, \.gql\)/);
    assert.match(hint, /OpenAPI \/ Swagger \(\.json, \.yaml, \.yml\)/);
});

test('a host that does not answer leaves the picker as it was', async () => {
    const h = harness();
    await h.loadSchemaImportKinds(() => Promise.resolve({ ok: false, status: 500 }));
    await h.loadSchemaImportKinds(() => Promise.reject(new Error('offline')));
    await h.loadSchemaImportKinds(() => Promise.resolve({ ok: true, json: () => Promise.resolve({ kinds: [] }) }));

    assert.equal(h.schemaUploadAccept(), '.proto');
});
