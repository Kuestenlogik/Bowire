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
    '\nreturn { summariseSchemaUpload, uploadSchemaFiles };\n',
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

test('each file goes to the endpoint its extension implies', async () => {
    const { uploadSchemaFiles } = harness();
    const seen = [];
    const post = (url) => {
        seen.push(url);
        return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ imported: 1 }) });
    };

    await uploadSchemaFiles([
        { name: 'a.proto', text: () => Promise.resolve('syntax = "proto3";') },
        { name: 'b.yaml', text: () => Promise.resolve('openapi: 3.0.0') },
        { name: 'c.JSON', text: () => Promise.resolve('{}') },
    ], post);

    assert.match(seen[0], /\/api\/proto\/upload\?name=a\.proto/);
    assert.match(seen[1], /\/api\/openapi\/upload\?name=b\.yaml/);
    assert.match(seen[2], /\/api\/openapi\/upload\?name=c\.JSON/);
});

test('the service count comes back from the server, not from the file list', () => {
    // The shape of the bug: the old code knew only how many files it had sent.
    const { uploadSchemaFiles } = harness();
    return uploadSchemaFiles(
        [{ name: 'a.proto', text: () => Promise.resolve('x') }],
        () => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ imported: 7 }) }),
    ).then(results => {
        assert.equal(results[0].imported, 7);
        assert.equal(results[0].ok, true);
    });
});

test('a rejected upload is recorded as such', async () => {
    const { uploadSchemaFiles } = harness();
    const results = await uploadSchemaFiles(
        [{ name: 'empty.proto', text: () => Promise.resolve('') }],
        () => Promise.resolve({ ok: false, status: 400, json: () => Promise.resolve({}) }),
    );

    assert.equal(results[0].ok, false);
    assert.equal(results[0].status, 400);
});

test('a network failure is a failure, not a silent success', async () => {
    const { uploadSchemaFiles } = harness();
    const results = await uploadSchemaFiles(
        [{ name: 'a.proto', text: () => Promise.resolve('x') }],
        () => Promise.reject(new Error('offline')),
    );

    assert.equal(results[0].ok, false);
});
