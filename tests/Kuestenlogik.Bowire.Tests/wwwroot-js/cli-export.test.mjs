// #538 — cli-export.js, the "Copy as Bowire CLI" generator.
//
// This is the JS half of a two-sided contract. Cli/cli-export-golden.json
// holds one { ctx, argv, rendered, notes } per scenario; this file replays
// every ctx through the real fragment, and Cli/CliExportGrammarTests.cs
// feeds the SAME argv arrays through the real System.CommandLine `call`
// command. Change the generator and this file goes red; rename a flag on
// `bowire call` and the C# side does.
//
// The fragment is concatenated inside prologue.js's IIFE in production, so
// every free identifier (substituteVars, getMergedVars, escapeShellSingleQuote,
// cliExportShell, cliExportKeepVars, uiMode) is module-scoped there. The
// sandbox below re-creates that scope: host names as `var` bindings on the
// wrapping scope, the fragment inlined, the testable surface returned.
//
// escapeShellSingleQuote genuinely lives in code-export.js, so that
// fragment is loaded too rather than stubbed — a divergence between the
// two quoters is exactly the kind of thing this should catch.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const FRAGMENT = (name) => readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/' + name), 'utf8');
const SRC = FRAGMENT('cli-export.js');
const CODE_EXPORT_SRC = FRAGMENT('code-export.js');

const GOLDEN = JSON.parse(readFileSync(
    resolve(__dirname, '../Cli/cli-export-golden.json'), 'utf8'));

// Minimal stand-ins for the workbench globals the fragment reads free.
// substituteVars mirrors the real resolver's contract closely enough for
// the export path: {{name}} / ${name} resolve from the merged map, unknown
// refs are left intact so the operator sees the typo.
function loadCliExport(state) {
    state = state || {};
    const prelude = `
        var _vars = state.vars || {};
        var uiMode = state.uiMode || 'standalone';
        var cliExportShell = state.shell || null;
        var cliExportKeepVars = !!state.keepVars;
        var selectedService = null, selectedMethod = null, requestMessages = [];
        var requestInputMode = 'json', serverUrls = [];
        // Shadow node's own global navigator (which reports the HOST
        // platform and would make the flavour tests machine-dependent).
        var navigator = state.platform ? { platform: state.platform } : undefined;
        function getMergedVars() { return _vars; }
        function getActiveEnv() { return null; }
        function $() { return null; }
        function $$() { return []; }
        function collectFormValuesFromState() { return null; }
        function substituteVars(input) {
            if (typeof input !== 'string') return input;
            return input
                .replace(/\\{\\{\\s*([^{}]+?)\\s*\\}\\}/g, function (m, k) {
                    return Object.prototype.hasOwnProperty.call(_vars, k) ? _vars[k] : m;
                })
                .replace(/\\$\\{\\s*([^}]+?)\\s*\\}/g, function (m, k) {
                    return Object.prototype.hasOwnProperty.call(_vars, k) ? _vars[k] : m;
                });
        }
    `;
    const postlude = `
        return {
            buildCliCommandSpec: buildCliCommandSpec,
            cliCommandArgv: cliCommandArgv,
            cliCommandString: cliCommandString,
            cliExportShellFlavour: cliExportShellFlavour,
            escapeShellPowerShell: escapeShellPowerShell,
            cliVarRefs: cliVarRefs,
            cliUrlCarriesHint: cliUrlCarriesHint,
            generateCliCommand: generateCliCommand,
            CODE_EXPORT_LANGUAGES: CODE_EXPORT_LANGUAGES,
            CODE_EXPORT_GENERATORS: CODE_EXPORT_GENERATORS
        };
    `;
    return new Function('state',
        prelude + '\n' + CODE_EXPORT_SRC + '\n' + SRC + '\n' + postlude)(state);
}

// ---- the golden fixture ----

test('golden fixture is non-empty (an emptied file must not pass vacuously)', () => {
    assert.ok(Array.isArray(GOLDEN.scenarios));
    assert.ok(GOLDEN.scenarios.length >= 10,
        'expected the fixture to cover every emitted flag; got ' + GOLDEN.scenarios.length);
});

for (const entry of GOLDEN.scenarios) {
    test(`golden: ${entry.scenario} → argv`, () => {
        const sb = loadCliExport({ vars: entry.vars, keepVars: entry.keepVars });
        const spec = sb.buildCliCommandSpec(entry.ctx, { keepVars: !!entry.keepVars });
        assert.deepEqual(sb.cliCommandArgv(spec), entry.argv);
    });

    test(`golden: ${entry.scenario} → rendered (posix)`, () => {
        const sb = loadCliExport({ vars: entry.vars, keepVars: entry.keepVars });
        const spec = sb.buildCliCommandSpec(entry.ctx, { keepVars: !!entry.keepVars });
        assert.equal(sb.cliCommandString(spec, 'posix'), entry.rendered);
    });

    test(`golden: ${entry.scenario} → notes`, () => {
        const sb = loadCliExport({ vars: entry.vars, keepVars: entry.keepVars });
        const spec = sb.buildCliCommandSpec(entry.ctx, { keepVars: !!entry.keepVars });
        assert.deepEqual(spec.notes, entry.notes || []);
    });
}

// Every flag the generator can emit has to appear somewhere in the
// fixture, or the C# grammar test can't prove it still parses.
test('fixture exercises every flag the generator emits', () => {
    const emitted = new Set();
    for (const entry of GOLDEN.scenarios) {
        for (const token of entry.argv) {
            if (typeof token === 'string' && token.startsWith('-')) emitted.add(token);
        }
    }
    for (const flag of ['--url', '--protocol', '--stream', '-d', '-H', '--var']) {
        assert.ok(emitted.has(flag), `no scenario emits ${flag}`);
    }
});

// ---- secret handling ----

test('secret refs survive substitution verbatim', () => {
    const sb = loadCliExport({ vars: { 'secret.tok': 'SHOULD-NEVER-APPEAR' } });
    const spec = sb.buildCliCommandSpec({
        serverUrl: 'https://api.example.com', service: 's', method: 'm',
        protocolId: 'rest', bodyJsonRaw: '{"t":"{{secret.tok}}"}', metadata: {}, messages: []
    }, {});
    assert.equal(spec.data[0], '{"t":"{{secret.tok}}"}');
    assert.ok(!spec.data[0].includes('SHOULD-NEVER-APPEAR'));
    assert.ok(spec.secretRefs);
});

test('keyring refs survive substitution verbatim', () => {
    const sb = loadCliExport({});
    const spec = sb.buildCliCommandSpec({
        serverUrl: 'https://api.example.com', service: 's', method: 'm',
        protocolId: 'rest', bodyJsonRaw: '{"t":"{{keyring.gh/deploy}}"}', metadata: {}, messages: []
    }, {});
    assert.equal(spec.data[0], '{"t":"{{keyring.gh/deploy}}"}');
    assert.ok(spec.secretRefs);
});

test('header values come from the RAW map, so a secret in a header never leaks', () => {
    // buildCodeExportContext runs substituteVars over ctx.metadata, which
    // resolves {{secret.*}} to the live credential. Reading that map here
    // would put the credential on the clipboard.
    const sb = loadCliExport({});
    const spec = sb.buildCliCommandSpec({
        serverUrl: 'https://api.example.com', service: 's', method: 'm', protocolId: 'rest',
        bodyJsonRaw: '{}',
        metadata: { Authorization: 'Bearer glpat-LIVE-CREDENTIAL' },
        metadataRaw: { Authorization: 'Bearer {{secret.deployToken}}' },
        messages: []
    }, {});
    assert.deepEqual(spec.headers, [['Authorization', 'Bearer {{secret.deployToken}}']]);
    assert.ok(spec.secretRefs);
    assert.ok(!sb.cliCommandString(spec, 'posix').includes('glpat-LIVE-CREDENTIAL'));
});

test('the sentinel never leaks into the output', () => {
    const sb = loadCliExport({ vars: { region: 'north' } });
    const spec = sb.buildCliCommandSpec({
        serverUrl: 'https://api.example.com', service: 's', method: 'm',
        protocolId: 'rest',
        bodyJsonRaw: '{"a":"{{secret.x}}","b":"{{region}}","c":"{{secret.y}}"}',
        metadata: {}, messages: []
    }, {});
    assert.equal(spec.data[0], '{"a":"{{secret.x}}","b":"north","c":"{{secret.y}}"}');
    assert.ok(!spec.data[0].includes('__BOWIRE_SECRET_'));
});

// ---- var-ref harvesting ----

test('cliVarRefs skips system, response and secret prefixes', () => {
    const sb = loadCliExport({});
    assert.deepEqual(
        sb.cliVarRefs('{{shipId}} ${region} {{uuid}} {{now+60}} {{response.id}} {{secret.tok}} {{runtime.now}}'),
        ['shipId', 'region']);
});

test('keep-variables only emits --var for names the environment actually defines', () => {
    const sb = loadCliExport({ vars: { known: 'yes' } });
    const spec = sb.buildCliCommandSpec({
        serverUrl: 'http://h:1', service: 's', method: 'm', protocolId: 'grpc',
        bodyJsonRaw: '{"a":"{{known}}","b":"{{typo}}"}', metadata: {}, metadataRaw: {}, messages: []
    }, { keepVars: true });
    assert.deepEqual(spec.vars, [['known', 'yes']]);
});

// ---- URL / hint composition (BowireServerUrl.Parse's three rules) ----

test('cliUrlCarriesHint mirrors BowireServerUrl.Parse', () => {
    const sb = loadCliExport({});
    assert.equal(sb.cliUrlCarriesHint('grpc@https://h'), true);
    // userinfo, not a hint
    assert.equal(sb.cliUrlCarriesHint('https://alice:pw@h'), false);
    // no scheme in the remainder → an email-style string, not a hinted URL
    assert.equal(sb.cliUrlCarriesHint('alice@example.com'), false);
    assert.equal(sb.cliUrlCarriesHint('https://h'), false);
});

test('an already-hinted server URL is not double-prefixed', () => {
    const sb = loadCliExport({});
    const spec = sb.buildCliCommandSpec({
        serverUrl: 'grpcweb@http://h:1', service: 's', method: 'm', protocolId: 'grpc',
        bodyJsonRaw: '{}', metadata: {}, messages: []
    }, {});
    assert.equal(spec.url, 'grpcweb@http://h:1');
    assert.equal(spec.protocol, '');
});

test('a URL carrying userinfo falls back to --protocol rather than composing a bad hint', () => {
    const sb = loadCliExport({});
    const spec = sb.buildCliCommandSpec({
        serverUrl: 'https://alice:pw@h/api', service: 's', method: 'm', protocolId: 'rest',
        bodyJsonRaw: '{}', metadata: {}, messages: []
    }, {});
    // 'rest@https://alice:pw@h/api' would still parse (Parse splits on the
    // FIRST '@'), so composing is safe here — the guard that matters is the
    // scheme-less case, covered by the mqtt golden scenario.
    assert.equal(spec.url, 'rest@https://alice:pw@h/api');
});

// ---- shell flavours ----

test('powershell doubles embedded single quotes; posix uses the backslash dance', () => {
    const sb = loadCliExport({});
    const spec = sb.buildCliCommandSpec({
        serverUrl: 'http://h:1', service: 's', method: 'm', protocolId: 'grpc',
        bodyJsonRaw: '{"note":"it\'s here"}', metadata: {}, messages: []
    }, {});
    assert.ok(sb.cliCommandString(spec, 'powershell').includes("'{\"note\":\"it''s here\"}'"));
    assert.ok(sb.cliCommandString(spec, 'posix').includes("'{\"note\":\"it'\\''s here\"}'"));
});

test('powershell continues lines with a backtick, posix with a backslash', () => {
    const sb = loadCliExport({});
    const spec = sb.buildCliCommandSpec({
        serverUrl: 'http://h:1', service: 's', method: 'm', protocolId: 'grpc',
        bodyJsonRaw: '{}', metadata: {}, messages: []
    }, {});
    assert.ok(sb.cliCommandString(spec, 'powershell').includes(' `\n  '));
    assert.ok(sb.cliCommandString(spec, 'posix').includes(' \\\n  '));
});

test('an explicit shell preference wins over platform detection', () => {
    assert.equal(
        loadCliExport({ shell: 'powershell', platform: 'Linux x86_64' }).cliExportShellFlavour(),
        'powershell');
    assert.equal(
        loadCliExport({ shell: 'posix', platform: 'Win32' }).cliExportShellFlavour(),
        'posix');
});

test('with no preference the flavour follows the client platform', () => {
    assert.equal(loadCliExport({ platform: 'Win32' }).cliExportShellFlavour(), 'powershell');
    assert.equal(loadCliExport({ platform: 'Windows' }).cliExportShellFlavour(), 'powershell');
    assert.equal(loadCliExport({ platform: 'Linux x86_64' }).cliExportShellFlavour(), 'posix');
    assert.equal(loadCliExport({ platform: 'MacIntel' }).cliExportShellFlavour(), 'posix');
});

test('with no preference and no navigator at all the flavour is posix', () => {
    // The render path must never throw on a headless / sandboxed client.
    assert.equal(loadCliExport({}).cliExportShellFlavour(), 'posix');
});

// ---- render-path safety ----

test('buildCliCommandSpec is pure: same ctx, same answer, no ctx mutation', () => {
    const sb = loadCliExport({ vars: { region: 'north' } });
    const ctx = {
        serverUrl: 'http://h:1', service: 's', method: 'm', protocolId: 'grpc',
        bodyJsonRaw: '{"r":"{{region}}"}', metadata: { a: 'b' }, metadataRaw: { a: 'b' },
        messages: ['{"r":"{{region}}"}']
    };
    const before = JSON.stringify(ctx);
    const first = sb.cliCommandArgv(sb.buildCliCommandSpec(ctx, {}));
    const second = sb.cliCommandArgv(sb.buildCliCommandSpec(ctx, {}));
    assert.deepEqual(first, second);
    assert.equal(JSON.stringify(ctx), before);
});

test('a context missing every optional field still produces a runnable line', () => {
    const sb = loadCliExport({});
    // The degenerate case the render path must survive: no service, no
    // metadata, no messages, no protocol.
    const out = sb.generateCliCommand({ method: 'ping' });
    assert.ok(out.includes('bowire call'));
    assert.ok(out.includes('/ping'));
});

// ---- registration in the shared code-export tables ----

test('every protocol offers the Bowire CLI language, and never as the default', () => {
    const sb = loadCliExport({});
    const langs = sb.CODE_EXPORT_LANGUAGES;
    for (const key of Object.keys(langs)) {
        const ids = langs[key].map((l) => l.id);
        assert.ok(ids.includes('bowire-cli'), `${key} is missing bowire-cli`);
        if (ids.length > 1) {
            assert.notEqual(ids[0], 'bowire-cli',
                `${key} would relabel the request-pane header button, which reads cmdLangs[0]`);
        }
    }
});

test('broker protocols no longer fall through to the REST curl generator', () => {
    const sb = loadCliExport({});
    for (const key of ['mqtt', 'nats', 'socketio']) {
        assert.deepEqual(sb.CODE_EXPORT_LANGUAGES[key].map((l) => l.id), ['bowire-cli']);
    }
});

test('the bowire-cli generator is registered and guarded', () => {
    const sb = loadCliExport({});
    assert.equal(typeof sb.CODE_EXPORT_GENERATORS['bowire-cli'], 'function');
    const out = sb.CODE_EXPORT_GENERATORS['bowire-cli']({
        serverUrl: 'http://h:1', service: 'S', method: 'M', protocolId: 'grpc',
        bodyJsonRaw: '{}', metadata: {}, messages: []
    });
    assert.ok(out.startsWith('bowire call'));
});
