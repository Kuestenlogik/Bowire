// #672 — the browser half of the script conformance suite.
//
// Fixtures/scripts/conformance.json is run here through scripts.js, the way
// the workbench runs a script, and by ScriptConformanceTests through
// Kuestenlogik.Bowire.Scripting, the way `bowire test` runs it. Both have to
// reach the outcome each case expects; a change to either engine that drifts
// from the other fails one of the two.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { compileFragment } from './_load-fragment.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const fixtures = JSON.parse(readFileSync(resolve(__dirname, '../Fixtures/scripts/conformance.json'), 'utf8'));

const load = compileFragment('../../../src/Kuestenlogik.Bowire/wwwroot/js/scripts.js', ['window'], '');

function run(c, index) {
    const window = {};
    load({ window });
    const api = window.__bowireScripts;
    const request = structuredClone(c.request);
    const env = structuredClone(c.env || {});
    const captured = structuredClone(c.captured || {});
    const opts = {
        source: c.source, protocolShape: c.shape, service: 'conformance', method: String(index),
        request, response: c.response ? structuredClone(c.response) : undefined, env, captured,
    };
    const result = c.phase === 'pre' ? api.runPreScript(opts) : api.runPostScript(opts);
    return {
        result, request, env, captured,
        log: api.getScriptConsole('conformance', String(index)).map((e) => e.message),
    };
}

fixtures.cases.forEach((c, index) => {
    if (c.serverOnly) return;
    test(`conformance: ${c.name}`, () => {
        const got = run(c, index);
        const e = c.expect;
        assert.equal(!!got.result.ok, e.ok);
        if ('assertFail' in e) assert.equal(!!got.result.assertFail, e.assertFail);
        if ('error' in e) assert.equal(got.result.error, e.error);
        if ('errorContains' in e) assert.match(got.result.error || '', new RegExp(e.errorContains));
        for (const [field, value] of Object.entries(e.request || {})) {
            assert.deepEqual(got.request[field], value, `request.${field}`);
        }
        if (e.env) assert.deepEqual(got.env, e.env);
        if (e.captured) assert.deepEqual(got.captured, e.captured);
        // A failing script also logs its error; the expected log is the
        // script's own ctx.log lines, which come first.
        if (e.log) assert.deepEqual(got.log.slice(0, e.log.length), e.log);
    });
});
