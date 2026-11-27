// #677 — the code export targets. Every scenario below runs through every
// target its protocol offers, through the real registry and the real
// context builder:
//
// - golden: Fixtures/code-export/golden.json holds each snippet; a change to
//   a generator shows up as a diff to review. BOWIRE_UPDATE_GOLDEN=1
//   rewrites it.
// - syntax: JS snippets go through `node --check`, Python through `ast`, Go
//   through `gofmt -e` where Go is installed; the rest are held by the golden
//   file.
// - no snippet carries a resolved secret, and a token fetched at request
//   time is a note, never a header.
// - every protocol offers at least three targets beyond the Bowire CLI, and
//   a plugin adds its own without touching core.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdtempSync, rmSync, existsSync, mkdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join } from 'node:path';
import { compileFragment, readFragment } from './_load-fragment.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const JS = (name) => '../../../src/Kuestenlogik.Bowire/wwwroot/js/' + name;
const GOLDEN_PATH = resolve(__dirname, '../Fixtures/code-export/golden.json');
const SECRET = 'S3CRET-VALUE-NEVER-IN-A-SNIPPET';

function load(vars) {
    const support = ['cli-export.js', 'http-file-export.js', 'code-export-http.js', 'code-export-messaging.js']
        .map((f) => readFragment(JS(f))).join('\n');
    return compileFragment(JS('code-export.js'), ['state', 'window'], support + `
        var _vars = state.vars || {};
        var uiMode = 'standalone', cliExportShell = 'posix', cliExportKeepVars = false, httpExportKeepVars = true;
        var selectedService = null, selectedMethod = null, serverUrls = [];
        function getMergedVars() { return _vars; }
        function getActiveEnv() { return null; }
        function substituteVars(input) {
            if (typeof input !== 'string') return input;
            return input.replace(/\\{\\{\\s*([^{}]+?)\\s*\\}\\}/g, function (m, k) {
                return Object.prototype.hasOwnProperty.call(_vars, k) ? String(_vars[k]) : m;
            });
        }
        return {
            ctxFrom: codeExportContextFrom,
            generate: generateCodeSnippetFor,
            targetsFor: codeExportTargetsFor,
            target: codeExportTarget
        };`)({ state: { vars: vars || {} }, window: {} });
}

const BASE_VARS = { base: 'https://api.example.com', 'secret.token': SECRET, 'keyring.svc/acct': SECRET };

const SCENARIOS = [
    { name: 'rest-post', raw: { source: 'rest', serverUrl: '{{base}}', service: 'Orders', method: 'Update', httpMethod: 'PUT', httpPath: '/orders/{id}',
        bodyJsonRaw: '{\n  "id": 7,\n  "item": "it\'s \\"quoted\\" $x"\n}', metadataRaw: { 'X-Trace': 'abc' } } },
    { name: 'rest-get', raw: { source: 'rest', serverUrl: 'https://api.example.com', service: 'Orders', method: 'List', httpMethod: 'GET', httpPath: '/orders',
        bodyJsonRaw: '{}', metadataRaw: { Accept: 'application/json' } } },
    { name: 'rest-secret', raw: { source: 'rest', serverUrl: 'https://api.example.com', service: 'Auth', method: 'Login', httpMethod: 'POST', httpPath: '/login',
        bodyJsonRaw: '{"key":"{{keyring.svc/acct}}"}', metadataRaw: { Authorization: 'Bearer {{secret.token}}' } } },
    { name: 'rest-oauth', raw: { source: 'rest', serverUrl: 'https://api.example.com', service: 'Me', method: 'Get', httpMethod: 'GET', httpPath: '/me',
        bodyJsonRaw: '{}', metadataRaw: {}, authKind: 'oauth2_cc' } },
    { name: 'odata-patch', raw: { source: 'odata', serverUrl: 'https://odata.example.com/svc', service: 'People', method: 'Update', httpMethod: 'PATCH',
        httpPath: '/People({key})', bodyJsonRaw: '{"key":"russell","FirstName":"R"}', metadataRaw: {} } },
    { name: 'graphql', raw: { source: 'graphql', serverUrl: 'https://gql.example.com/graphql', service: 'Query', method: 'user',
        bodyJsonRaw: '{"query":"query { user(id: 1) { name } }","variables":{}}', metadataRaw: {} } },
    { name: 'jsonrpc', raw: { source: 'jsonrpc', serverUrl: 'http://rpc.example.com/rpc', service: 'Methods', method: 'sum',
        bodyJsonRaw: '[1, 2]', metadataRaw: {} } },
    { name: 'mcp-tool', raw: { source: 'mcp', serverUrl: 'http://localhost:3000/mcp', service: 'Tools', method: 'search',
        bodyJsonRaw: '{"q":"bowire"}', metadataRaw: {} } },
    { name: 'sse', raw: { source: 'sse', serverUrl: 'http://localhost:5000', service: 'Events', method: 'SSE/events/stream',
        bodyJsonRaw: '{}', metadataRaw: { 'X-Tenant': 't1' }, serverStreaming: true } },
    { name: 'grpc-unary', raw: { source: 'grpc', serverUrl: 'http://localhost:5001', service: 'greet.Greeter', method: 'SayHello',
        bodyJsonRaw: '{"name":"world"}', metadataRaw: { 'X-Api-Key': 'k' }, inputTypeName: 'HelloRequest', outputTypeName: 'HelloReply' } },
    { name: 'grpc-server-stream', raw: { source: 'grpc', serverUrl: 'https://grpc.example.com', service: 'greet.Greeter', method: 'SayHellos',
        bodyJsonRaw: '{"name":"world","count":3}', metadataRaw: {}, serverStreaming: true, inputTypeName: 'HelloRequest', outputTypeName: 'HelloReply' } },
    { name: 'websocket', raw: { source: 'websocket', serverUrl: 'http://localhost:5000', service: 'WebSocket', method: '/ws/echo',
        bodyJsonRaw: '{"type":"ping"}', metadataRaw: {} } },
    { name: 'signalr', raw: { source: 'signalr', serverUrl: 'http://localhost:5000/hubs/chat', service: 'ChatHub', method: 'Send',
        bodyJsonRaw: '{"user":"ada","text":"hi"}', metadataRaw: {}, inputFieldNames: ['user', 'text'] } },
    { name: 'mqtt-publish', raw: { source: 'mqtt', serverUrl: 'mqtt://broker.example.com:1883', service: 'sensors', method: 'sensors/temp',
        methodFullName: 'mqtt/sensors/temp/publish', bodyJsonRaw: '{"payload":"21.5","qos":1,"retain":true}', metadataRaw: {} } },
    { name: 'mqtt-subscribe', raw: { source: 'mqtt', serverUrl: 'mqtt://broker.example.com', service: 'sensors', method: 'sensors/#',
        methodFullName: 'mqtt/sensors/#/subscribe', bodyJsonRaw: '{"qos":0}', metadataRaw: {}, serverStreaming: true } },
    { name: 'nats-request', raw: { source: 'nats', serverUrl: 'nats://localhost:4222', service: 'orders', method: 'orders.get/request',
        methodFullName: 'nats/orders.get/request', bodyJsonRaw: '{"payload":"{\\"id\\":7}"}', metadataRaw: {} } },
    { name: 'nats-jetstream', raw: { source: 'nats', serverUrl: 'nats://localhost:4222', service: 'ORDERS', method: 'orders.created',
        methodFullName: 'nats/jetstream/ORDERS/publish/orders.created', bodyJsonRaw: '{"payload":"new"}', metadataRaw: {} } },
    { name: 'socketio-emit', raw: { source: 'socketio', serverUrl: 'http://localhost:3000', service: 'Socket.IO', method: 'emit',
        methodFullName: 'socketio/emit', bodyJsonRaw: '{"event":"chat","data":"{\\"text\\":\\"hi\\"}"}', metadataRaw: {} } },
    { name: 'socketio-listen', raw: { source: 'socketio', serverUrl: 'http://localhost:3000', service: 'Socket.IO', method: 'chat',
        methodFullName: 'socketio/chat/listen', bodyJsonRaw: '{}', metadataRaw: {}, serverStreaming: true } },
    { name: 'pulsar-produce', raw: { source: 'pulsar', serverUrl: 'pulsar://localhost:6650', service: 'orders', method: 'produce',
        methodFullName: 'pulsar/topic/persistent://public/default/orders/produce', bodyJsonRaw: '{"payload":"hello"}', metadataRaw: {} } },
];

const api = load(BASE_VARS);

function snippetsOf(scenario) {
    const out = {};
    for (const t of api.targetsFor(scenario.raw.source)) {
        out[t.id] = api.generate(t.id, api.ctxFrom(scenario.raw));
    }
    return out;
}

const ALL = Object.fromEntries(SCENARIOS.map((s) => [s.name, snippetsOf(s)]));

if (process.env.BOWIRE_UPDATE_GOLDEN === '1') {
    mkdirSync(dirname(GOLDEN_PATH), { recursive: true });
    writeFileSync(GOLDEN_PATH, JSON.stringify({
        _comment: '#677 — every code export snippet for the scenarios in code-export-targets.test.mjs. '
            + 'Regenerate with BOWIRE_UPDATE_GOLDEN=1 and review the diff.',
        scenarios: ALL,
    }, null, 2) + '\n');
}

const GOLDEN = JSON.parse(readFileSync(GOLDEN_PATH, 'utf8')).scenarios;

test('the golden file covers every scenario and target', () => {
    assert.deepEqual(Object.keys(GOLDEN).sort(), Object.keys(ALL).sort());
    for (const [name, snippets] of Object.entries(ALL)) {
        assert.deepEqual(Object.keys(GOLDEN[name]).sort(), Object.keys(snippets).sort(), name);
    }
});

for (const [name, snippets] of Object.entries(ALL)) {
    test(`golden: ${name}`, () => {
        for (const [id, text] of Object.entries(snippets)) {
            assert.equal(text, GOLDEN[name][id], `${name} / ${id}`);
        }
    });
}

test('every protocol offers at least three targets beyond the Bowire CLI', () => {
    for (const protocol of ['rest', 'odata', 'graphql', 'jsonrpc', 'mcp', 'sse', 'grpc', 'websocket', 'signalr',
        'mqtt', 'nats', 'socketio', 'pulsar']) {
        const ids = api.targetsFor(protocol).map((t) => t.id).filter((id) => id !== 'bowire-cli');
        assert.ok(ids.length >= 3, `${protocol}: ${ids}`);
    }
});

test('the HTTP family offers the HTTPSnippet set', () => {
    const ids = api.targetsFor('rest').map((t) => t.id);
    for (const id of ['curl', 'fetch', 'axios', 'python', 'python-httpx', 'go', 'java-http', 'java-okhttp', 'csharp-http',
        'csharp-restsharp', 'php-curl', 'ruby', 'rust-reqwest', 'swift', 'kotlin-okhttp', 'powershell', 'httpie', 'wget']) {
        assert.ok(ids.includes(id), id);
    }
});

test('no snippet carries a resolved secret; the references stay and a note says so', () => {
    for (const [name, snippets] of Object.entries(ALL)) {
        for (const [id, text] of Object.entries(snippets)) {
            assert.ok(!text.includes(SECRET), `${name} / ${id} leaks the secret`);
        }
    }
    for (const [id, text] of Object.entries(ALL['rest-secret'])) {
        if (id === 'bowire-cli' || id === 'http-file') continue;
        assert.ok(text.includes('{{secret.token}}') && text.includes('{{keyring.svc/acct}}'), id);
        assert.match(text, /references are left as they are/, id);
    }
});

test('a token fetched at request time is a note, not a header', () => {
    for (const [id, text] of Object.entries(ALL['rest-oauth'])) {
        if (id === 'bowire-cli' || id === 'http-file') continue;
        assert.match(text, /fetches its token at request time \(oauth2_cc\)/, id);
        assert.ok(!/Authorization/.test(text.split('\n').filter((l) => !/^\s*(#|\/\/)/.test(l)).join('\n')), id);
    }
});

test('JSON-RPC and MCP send the envelope their plugin builds', () => {
    assert.ok(ALL.jsonrpc.curl.includes(`'{"jsonrpc":"2.0","id":1,"method":"sum","params":[1,2]}'`), ALL.jsonrpc.curl);
    assert.ok(ALL['mcp-tool'].curl.includes('"method":"tools/call","params":{"name":"search","arguments":{"q":"bowire"}}'), ALL['mcp-tool'].curl);
    assert.match(ALL['mcp-tool'].curl, /initialize request first/);
});

test('a plugin adds its own target without touching core', () => {
    const plugin = load({});
    const win = {};
    const registered = compileFragment(JS('code-export.js'), ['window'], `return window.bowireCodeExport;`)({ window: win });
    registered.register({ id: 'kafka-cli', label: 'kcat', protocols: ['kafka'], comment: '#', rank: 10,
        generate: (ctx) => 'kcat -P -t ' + ctx.method });
    assert.deepEqual(registered.targetsFor('kafka').map((t) => t.id), ['kafka-cli']);
    assert.ok(plugin.targetsFor('kafka').every((t) => t.id === 'bowire-cli'));
});

// ---- syntax ----

function snippetsBy(ids) {
    const out = [];
    for (const [name, snippets] of Object.entries(ALL)) {
        for (const [id, text] of Object.entries(snippets)) if (ids.includes(id)) out.push({ name, id, text });
    }
    return out;
}

const dir = mkdtempSync(join(tmpdir(), 'bowire-code-export-'));
process.on('exit', () => rmSync(dir, { recursive: true, force: true }));

test('the JavaScript snippets parse', () => {
    const list = snippetsBy(['fetch', 'axios', 'js-eventsource', 'js-ws', 'js-signalr', 'node-grpc', 'node-mqtt', 'node-nats', 'node-socketio']);
    assert.ok(list.length > 20);
    list.forEach((s, i) => {
        const file = join(dir, `s${i}.mjs`);
        writeFileSync(file, s.text);
        const r = spawnSync(process.execPath, ['--check', file], { encoding: 'utf8' });
        assert.equal(r.status, 0, `${s.name} / ${s.id}\n${r.stderr}\n${s.text}`);
    });
});

function findPython() {
    for (const cmd of ['python', 'python3']) {
        const r = spawnSync(cmd, ['--version'], { encoding: 'utf8' });
        if (r.status === 0 && /Python 3/.test(r.stdout + r.stderr)) return cmd;
    }
    return null;
}

test('the Python snippets parse', (t) => {
    const python = findPython();
    if (!python) { t.skip('no Python 3 on this machine'); return; }
    const list = snippetsBy(['python', 'python-httpx', 'python-sse', 'python-grpc', 'python-websockets', 'python-paho',
        'python-nats', 'python-socketio', 'python-pulsar']);
    assert.ok(list.length > 15);
    for (const s of list) {
        const r = spawnSync(python, ['-I', '-c', 'import ast, sys; ast.parse(sys.stdin.read())'], { input: s.text, encoding: 'utf8' });
        assert.equal(r.status, 0, `${s.name} / ${s.id}\n${r.stderr}\n${s.text}`);
    }
});

function findGofmt() {
    for (const cmd of ['gofmt', 'C:/Program Files/Go/bin/gofmt.exe', '/usr/local/go/bin/gofmt']) {
        if (cmd.includes('/') && !existsSync(cmd)) continue;
        const r = spawnSync(cmd, ['-l'], { input: 'package main\n', encoding: 'utf8' });
        if (r.status === 0) return cmd;
    }
    return null;
}

test('the Go snippets parse', (t) => {
    const gofmt = findGofmt();
    if (!gofmt) { t.skip('no Go toolchain on this machine'); return; }
    const list = snippetsBy(['go', 'go-grpc', 'go-nats']);
    assert.ok(list.length > 10);
    for (const s of list) {
        const r = spawnSync(gofmt, ['-e'], { input: s.text, encoding: 'utf8' });
        assert.equal(r.status, 0, `${s.name} / ${s.id}\n${r.stderr}\n${s.text}`);
    }
});
