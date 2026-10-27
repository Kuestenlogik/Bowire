// #673 — http-file-export.js, the "Copy as .http" generator.
//
// The JS half of a two-sided contract, like cli-export.test.mjs.
// Kuestenlogik.Bowire.Interop.Tests/Fixtures/http-snippet-golden.json holds
// one { ctx, vars, keepVars, expected, verb, url } per scenario; this file
// replays every ctx through the real fragment, and HttpSnippetGoldenTests.cs
// parses every `expected` block with the importer's own .http parser. Change
// the generator and this side goes red; change the parser so it no longer
// reads what the workbench writes and the C# side does.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { compileFragment, readFragment } from './_load-fragment.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const _RELPATH = (name) => '../../../src/Kuestenlogik.Bowire/wwwroot/js/' + name;
// cliResolveVars and its secret masking genuinely live in cli-export.js.
const SUPPORT_SRC = readFragment(_RELPATH('cli-export.js')) + '\n' + readFragment(_RELPATH('code-export.js'));

const GOLDEN = JSON.parse(readFileSync(
    resolve(__dirname, '../../Kuestenlogik.Bowire.Interop.Tests/Fixtures/http-snippet-golden.json'), 'utf8'));

// The workbench globals the fragments read free. substituteVars mirrors the
// real resolver closely enough for export: known refs resolve, unknown refs
// stay as written.
function loadHttpExport(state) {
    state = state || {};
    const prelude = `
        var _vars = state.vars || {};
        var uiMode = 'standalone', cliExportShell = null, cliExportKeepVars = !!state.keepVars;
        var selectedService = null, selectedMethod = null, serverUrls = [];
        function getMergedVars() { return _vars; }
        function getActiveEnv() { return null; }
        function $() { return null; }
        function $$() { return []; }
        function substituteVars(input) {
            if (typeof input !== 'string') return input;
            return input
                .replace(/\\{\\{\\s*([^{}]+?)\\s*\\}\\}/g, function (m, k) {
                    return Object.prototype.hasOwnProperty.call(_vars, k) ? String(_vars[k]) : m;
                })
                .replace(/\\$\\{\\s*([^}]+?)\\s*\\}/g, function (m, k) {
                    return Object.prototype.hasOwnProperty.call(_vars, k) ? String(_vars[k]) : m;
                });
        }
    `;
    const postlude = `
        return {
            generateHttpFileSnippet: generateHttpFileSnippet,
            CODE_EXPORT_LANGUAGES: CODE_EXPORT_LANGUAGES,
            CODE_EXPORT_GENERATORS: CODE_EXPORT_GENERATORS
        };
    `;
    return compileFragment(
        _RELPATH('http-file-export.js'),
        ['state'],
        SUPPORT_SRC + '\n' + prelude + '\n' + postlude
    )({ state });
}

test('golden fixture is non-empty (an emptied file must not pass vacuously)', () => {
    assert.ok(Array.isArray(GOLDEN.scenarios));
    assert.ok(GOLDEN.scenarios.length >= 6, 'got ' + GOLDEN.scenarios.length);
});

for (const entry of GOLDEN.scenarios) {
    test(`golden: ${entry.scenario}`, () => {
        const sb = loadHttpExport({ vars: entry.vars, keepVars: entry.keepVars });
        assert.equal(sb.generateHttpFileSnippet(entry.ctx, { keepVars: !!entry.keepVars }), entry.expected);
    });
}

test('a secret value never reaches the text, with or without keepVars', () => {
    for (const keepVars of [false, true]) {
        const sb = loadHttpExport({ vars: { 'secret.tok': 'SHOULD-NEVER-APPEAR', 'keyring.svc/acct': 'NOR-THIS' } });
        const out = sb.generateHttpFileSnippet({
            serverUrl: 'https://api.example.com', method: 'Ping', httpMethod: 'POST', httpPath: '/ping', source: 'rest',
            bodyJsonRaw: '{"t":"{{secret.tok}}","k":"{{keyring.svc/acct}}"}',
            metadataRaw: { Authorization: 'Bearer {{secret.tok}}' }
        }, { keepVars });
        assert.ok(!out.includes('SHOULD-NEVER-APPEAR') && !out.includes('NOR-THIS'), out);
        assert.ok(out.includes('{{tok}}') && out.includes('{{svc_acct}}'), out);
    }
});

test('.http is offered for REST and GraphQL, and the generator map routes it', () => {
    const sb = loadHttpExport({});
    assert.ok(sb.CODE_EXPORT_LANGUAGES.rest.some((l) => l.id === 'http-file'));
    assert.ok(sb.CODE_EXPORT_LANGUAGES.graphql.some((l) => l.id === 'http-file'));
    const out = sb.CODE_EXPORT_GENERATORS['http-file']({
        serverUrl: 'https://x.test', method: 'Ping', httpMethod: 'GET', httpPath: '/ping', source: 'rest', bodyJsonRaw: '{}', metadataRaw: {}
    });
    assert.ok(out.includes('GET https://x.test/ping'), out);
});

test('the Code tab keeps {{variables}} by default, and the pill turns it off', () => {
    const ctx = {
        serverUrl: '{{base}}', method: 'Ping', httpMethod: 'GET', httpPath: '/ping', source: 'rest', bodyJsonRaw: '{}', metadataRaw: {}
    };
    const byDefault = loadHttpExport({ vars: { base: 'https://x.test' } });
    assert.ok(byDefault.CODE_EXPORT_GENERATORS['http-file'](ctx).includes('GET {{base}}/ping'));
    const resolved = compileFragment(
        '../../../src/Kuestenlogik.Bowire/wwwroot/js/http-file-export.js', ['state'],
        SUPPORT_SRC + `
        var _vars = state.vars, httpExportKeepVars = false, cliExportKeepVars = false, cliExportShell = null, uiMode = 'standalone';
        function getMergedVars() { return _vars; }
        function substituteVars(input) { return typeof input === 'string' ? input.split('{{base}}').join(_vars.base) : input; }
        return { CODE_EXPORT_GENERATORS: CODE_EXPORT_GENERATORS };`
    )({ state: { vars: { base: 'https://x.test' } } });
    assert.ok(resolved.CODE_EXPORT_GENERATORS['http-file'](ctx).includes('GET https://x.test/ping'));
});
