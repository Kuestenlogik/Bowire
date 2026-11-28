// #810 — mcp-apps.js: hosting an MCP App.
//
// The fragment reads the ui:// resource through /api/invoke, hands its HTML
// and CSP to the view store, frames the view sandboxed, wires the AppBridge
// (tool input and result out, the app's own tools/call back through
// /api/invoke) and logs the traffic. The bridge, the DOM and fetch are fakes
// here; the browser check runs the real ones against the MCP sample.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const HOST = ['config', 'render', 'applyAuth', 'el', 'afterRender', 'document', 'window', 'performance', 'fetch', 'atob', 'mcpParseFrame'];
const load = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/mcp-apps.js',
    HOST,
    'return { mcpAppStart, mcpAppRunFor, mcpAppResultMessage, mcpAppArguments, mcpAppWithoutNulls, renderMcpAppPane };',
);

function fakeFrame() {
    const attrs = {};
    const contentWindow = { name: 'app-window' };
    return {
        className: '', style: {}, src: '', contentWindow,
        setAttribute(k, v) { attrs[k] = v; },
        attrs,
    };
}

function setup({ resource }) {
    const calls = [];
    const listeners = [];
    let bridge = null;
    let frame = null;
    let renders = 0;
    class AppBridge {
        constructor(client, hostInfo, caps) { this.client = client; this.hostInfo = hostInfo; this.caps = caps; this.sent = []; bridge = this; }
        async connect(transport) { this.transport = transport; }
        sendToolInput(p) { this.sent.push(['input', p]); }
        sendToolResult(p) { this.sent.push(['result', p]); }
    }
    class PostMessageTransport {
        constructor(target, source) { this.target = target; this.source = source; this.wire = []; }
        async send(message) { this.wire.push(message); }
    }
    const B = { AppBridge, PostMessageTransport, buildAllowAttribute: (p) => (p && p.camera ? 'camera' : '') };
    const json = (status, body) => ({ ok: status < 400, status, json: async () => body });
    const host = {
        config: { prefix: '/bowire', version: '9.9.9' },
        render: () => { renders++; },
        applyAuth: async (m) => ({ ...m, authorization: 'Bearer env' }),
        el: (tag, props, ...children) => ({ tag, props, children }),
        afterRender: (fn) => fn(),
        document: {
            createElement: (tag) => { assert.equal(tag, 'iframe'); frame = fakeFrame(); return frame; },
            getElementById: () => null,
            head: { appendChild() {} },
        },
        window: {
            BowireMcpAppBridge: B,
            addEventListener: (type, fn) => listeners.push([type, fn]),
            open() {},
        },
        performance: { now: () => 1000 },
        atob: (s) => Buffer.from(s, 'base64').toString('utf8'),
        mcpParseFrame: (msg) => JSON.parse(msg.data),
        fetch: async (url, init) => {
            const body = JSON.parse(init.body);
            calls.push({ url, body });
            if (url.startsWith('/bowire/api/invoke')) {
                if (body.service === 'Resources') return json(200, { response: JSON.stringify(resource) });
                return json(200, { response: JSON.stringify({ content: [{ type: 'text', text: '5' }] }) });
            }
            if (url === '/bowire/api/ui/mcp-apps/views') return json(200, { id: 'V1', url: '/bowire/api/ui/mcp-apps/views/V1' });
            return json(404, {});
        },
    };
    const m = load(host);
    return { m, calls, listeners, get bridge() { return bridge; }, get frame() { return frame; }, get renders() { return renders; } };
}

const resultMsg = { data: JSON.stringify({ event: 'result', data: { content: [{ type: 'text', text: 'r' }] } }) };
const S = { streamMessages: [{ data: JSON.stringify({ event: 'log', data: {} }) }, resultMsg], requestMessages: ['{"sensorId":"dock-1"}'] };
const svc = { source: 'mcp', originUrl: 'http://srv/mcp' };
const method = { name: 'dock_board', uiResourceUri: 'ui://s/board.html' };

test('a run belongs to the result frame and carries the call arguments', () => {
    const { m } = setup({ resource: {} });
    const run = m.mcpAppRunFor(S, svc, method);
    assert.equal(run.resourceUri, 'ui://s/board.html');
    assert.deepEqual(run.args, { sensorId: 'dock-1' });
    assert.deepEqual(run.result, { content: [{ type: 'text', text: 'r' }] });
    assert.equal(m.mcpAppRunFor(S, svc, method), run, 'a re-render reuses the run');
    assert.equal(m.mcpAppRunFor({ streamMessages: [] }, svc, method), null, 'no result, no app');
});

test('a tool without a UI, or a non-MCP service, gets no pane', () => {
    const { m } = setup({ resource: {} });
    assert.equal(m.renderMcpAppPane(S, svc, { name: 'echo' }, (x) => x), null);
    assert.equal(m.renderMcpAppPane(S, { source: 'rest' }, method, (x) => x), null);
});

test('the app is read, hosted under its CSP, framed sandboxed and wired to the bridge', async () => {
    const csp = { connectDomains: [] };
    const env = setup({ resource: { contents: [{ uri: 'ui://s/board.html', text: '<p>b</p>', _meta: { ui: { csp, permissions: { camera: {} } } } }] } });
    const run = env.m.mcpAppRunFor(S, svc, method);
    const mount = { children: [], appendChild(c) { this.children.push(c); } };

    await env.m.mcpAppStart(run, mount);

    assert.equal(run.status, 'loading', run.error);
    const [read, view] = env.calls;
    assert.equal(read.url, '/bowire/api/invoke?serverUrl=' + encodeURIComponent('http://srv/mcp'));
    assert.deepEqual([read.body.service, read.body.method, read.body.protocol], ['Resources', 'ui://s/board.html', 'mcp']);
    assert.equal(read.body.metadata.authorization, 'Bearer env', 'the environment credentials go along');
    assert.deepEqual(view.body, { html: '<p>b</p>', csp });

    assert.equal(env.frame.attrs.sandbox, 'allow-scripts allow-forms');
    assert.equal(env.frame.attrs.allow, 'camera');
    assert.equal(env.frame.src, '/bowire/api/ui/mcp-apps/views/V1');
    assert.equal(mount.children[0], env.frame);
    assert.equal(env.bridge.transport.target, env.frame.contentWindow);
    assert.equal(env.bridge.hostInfo.version, '9.9.9');
});

test('once the app is initialised it gets the input and the result; its tools/call goes through /api/invoke', async () => {
    const env = setup({ resource: { contents: [{ text: '<p>b</p>' }] } });
    const run = env.m.mcpAppRunFor(S, svc, method);
    await env.m.mcpAppStart(run, { appendChild() {} });

    env.bridge.oninitialized();
    assert.equal(run.status, 'running');
    assert.deepEqual(env.bridge.sent, [['input', { arguments: { sensorId: 'dock-1' } }], ['result', run.result]]);

    const answer = await env.bridge.oncalltool({ name: 'add', arguments: { a: 2, b: 3 } });
    assert.deepEqual(answer, { content: [{ type: 'text', text: '5' }] });
    const call = env.calls.at(-1);
    assert.deepEqual([call.body.service, call.body.method, JSON.parse(call.body.messages[0])], ['Tools', 'add', { a: 2, b: 3 }]);
});

test('the traffic log records both directions with method and id', async () => {
    const env = setup({ resource: { contents: [{ text: '<p>b</p>' }] } });
    const run = env.m.mcpAppRunFor(S, svc, method);
    await env.m.mcpAppStart(run, { appendChild() {} });

    await env.bridge.transport.send({ jsonrpc: '2.0', id: 1, result: {} });
    const [type, onMessage] = env.listeners.find(([t]) => t === 'message');
    assert.equal(type, 'message');
    onMessage({ source: env.frame.contentWindow, data: { jsonrpc: '2.0', id: 2, method: 'tools/call', params: {} } });
    onMessage({ source: { other: true }, data: { jsonrpc: '2.0', method: 'ignored' } });

    assert.deepEqual(run.log.map((e) => [e.direction, e.method, e.id]), [['out', 'response', 1], ['in', 'tools/call', 2]]);
    assert.equal(env.bridge.transport.wire.length, 1, 'logging does not swallow the message');
});

test('a resource without HTML fails the run with a reason', async () => {
    const env = setup({ resource: { contents: [{ uri: 'ui://s/board.html' }] } });
    const run = env.m.mcpAppRunFor(S, svc, method);
    await env.m.mcpAppStart(run, { appendChild() {} });
    assert.equal(run.status, 'failed');
    assert.match(run.error, /mcpApp\.noHtml|board\.html/);
});

test('what goes to an app leaves out the nulls the SDK writes for unset fields', async () => {
    assert.deepEqual(setup({ resource: {} }).m.mcpAppWithoutNulls(
        { content: [{ type: 'text', text: 'x', annotations: null }], _meta: null, isError: false, n: 0 }),
        { content: [{ type: 'text', text: 'x' }], isError: false, n: 0 });

    const env = setup({ resource: { contents: [{ text: '<p>b</p>', _meta: null }] } });
    const withNulls = { data: JSON.stringify({ event: 'result', data: { content: [], structuredContent: null, _meta: null } }) };
    const run = env.m.mcpAppRunFor({ streamMessages: [withNulls], requestMessages: ['{}'] }, svc, method);
    assert.deepEqual(run.result, { content: [] });
    await env.m.mcpAppStart(run, { appendChild() {} });
    assert.equal(run.status, 'loading', run.error);
});
