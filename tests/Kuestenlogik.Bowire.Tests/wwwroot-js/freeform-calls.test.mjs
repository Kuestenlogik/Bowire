// #995 — streaming and channel calls of the classic freeform form.
//
// The form offered a method type for every protocol but always called
// /api/invoke, so a server-streaming or duplex call typed in by hand ran as
// unary. These pin the call mode per method type, the URLs and bodies the
// form sends, and the lifecycle of a stream and a channel against fake
// EventSource and fetch.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const appended = `
    var config = { prefix: '/bowire' };
    var _renders = 0;
    function render() { _renders++; }
    var _toasts = [];
    function toast(msg, kind) { _toasts.push([msg, kind]); }
    var EventSource = opts.EventSource;
    var fetch = opts.fetch;
    function el(tag, props) {
        var node = { tag: tag, props: props || {}, children: [] };
        node.appendChild = function (c) { node.children.push(c); };
        return node;
    }
    function highlightJsonInteractive(s) { return String(s); }
    return {
        freeformCall: freeformCall,
        freeformCallMode: freeformCallMode,
        buildFreeformStreamUrl: buildFreeformStreamUrl,
        buildFreeformChannelOpenBody: buildFreeformChannelOpenBody,
        startFreeformStream: startFreeformStream,
        openFreeformChannel: openFreeformChannel,
        sendFreeformChannel: sendFreeformChannel,
        stopFreeformCall: stopFreeformCall,
        freeformCallActive: freeformCallActive,
        freeformExecuteLabel: freeformExecuteLabel,
        renderFreeformFrames: renderFreeformFrames,
        toasts: function () { return _toasts; }
    };
`;
const loadFragment = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/freeform-calls.js', ['opts'], appended);
const load = (opts) => loadFragment({ opts });

/** A fake EventSource that records what it was opened on and lets the test push events. */
function fakeEventSources() {
    const opened = [];
    class FakeEventSource {
        constructor(url) {
            this.url = url;
            this.closed = false;
            this.listeners = {};
            opened.push(this);
        }
        addEventListener(type, fn) { this.listeners[type] = fn; }
        close() { this.closed = true; }
        frame(obj) { this.onmessage({ data: JSON.stringify(obj) }); }
        done() { this.listeners.done({ data: '{}' }); }
    }
    return { FakeEventSource, opened };
}

/** A fake fetch that answers by URL and records every call. */
function fakeFetch(answers) {
    const calls = [];
    const fn = async (url, init) => {
        calls.push({ url, init });
        const key = Object.keys(answers).find((k) => url.includes(k));
        const answer = key ? answers[key] : { ok: true, body: {} };
        return { ok: answer.ok !== false, status: answer.status || 200, json: async () => answer.body };
    };
    return { fn, calls };
}

const nats = {
    protocol: 'nats', service: '', method: 'orders.created', methodType: 'ServerStreaming',
    serverUrl: 'nats://localhost:4222', body: '{}', metadata: { queue_group: 'workers' },
};

test('method types map to unary, stream and channel calls', () => {
    const m = load({});
    assert.equal(m.freeformCallMode('Unary'), 'unary');
    assert.equal(m.freeformCallMode('ServerStreaming'), 'stream');
    assert.equal(m.freeformCallMode('ClientStreaming'), 'channel');
    assert.equal(m.freeformCallMode('Duplex'), 'channel');
    assert.equal(m.freeformCallMode(undefined), 'unary');
});

test('the stream URL carries service, method, body, protocol, metadata and server', () => {
    const m = load({});
    const url = new URL(m.buildFreeformStreamUrl('/bowire', nats), 'http://localhost');
    assert.equal(url.pathname, '/bowire/api/invoke/stream');
    assert.equal(url.searchParams.get('method'), 'orders.created');
    assert.equal(url.searchParams.get('protocol'), 'nats');
    assert.equal(url.searchParams.get('serverUrl'), 'nats://localhost:4222');
    assert.deepEqual(JSON.parse(url.searchParams.get('messages')), ['{}']);
    assert.deepEqual(JSON.parse(url.searchParams.get('metadata')), { queue_group: 'workers' });
});

test('no metadata means no metadata parameter', () => {
    const m = load({});
    const url = new URL(m.buildFreeformStreamUrl('/bowire', { ...nats, metadata: {} }), 'http://localhost');
    assert.equal(url.searchParams.has('metadata'), false);
});

test('a stream collects its frames and ends on done', () => {
    const { FakeEventSource, opened } = fakeEventSources();
    const m = load({ EventSource: FakeEventSource });

    m.startFreeformStream(nats);
    assert.equal(m.freeformCall.mode, 'stream');
    assert.equal(m.freeformCallActive(), true);
    assert.equal(m.freeformExecuteLabel(nats), 'Stop');

    opened[0].frame({ index: 0, data: { id: 1 } });
    opened[0].frame({ index: 1, data: { id: 2 } });
    opened[0].done();

    assert.deepEqual(m.freeformCall.frames.map((f) => f.data), [{ id: 1 }, { id: 2 }]);
    assert.equal(m.freeformCall.state, 'done');
    assert.equal(opened[0].closed, true);
    assert.equal(m.freeformExecuteLabel(nats), 'Execute');
});

test('a stream that reports an error ends as an error, with the message', () => {
    const { FakeEventSource, opened } = fakeEventSources();
    const m = load({ EventSource: FakeEventSource });

    m.startFreeformStream(nats);
    opened[0].onerror({ data: JSON.stringify({ error: 'no route to broker' }) });

    assert.equal(m.freeformCall.state, 'error');
    assert.equal(m.freeformCall.error, 'no route to broker');
    assert.equal(opened[0].closed, true);
});

test('stopping a stream closes it', () => {
    const { FakeEventSource, opened } = fakeEventSources();
    const m = load({ EventSource: FakeEventSource });

    m.startFreeformStream(nats);
    m.stopFreeformCall();

    assert.equal(opened[0].closed, true);
    assert.equal(m.freeformCallActive(), false);
});

const signalr = {
    protocol: 'signalr', service: '/chathub', method: 'Echo', methodType: 'Duplex',
    serverUrl: 'http://localhost:5000', body: '"hi"', metadata: { Authorization: 'Bearer x' },
};

test('a channel opens with service, method, protocol and metadata, then sends and closes', async () => {
    const { FakeEventSource, opened } = fakeEventSources();
    const { fn, calls } = fakeFetch({ '/api/channel/open': { body: { channelId: 'c1' } } });
    const m = load({ EventSource: FakeEventSource, fetch: fn });

    assert.equal(m.freeformExecuteLabel(signalr), 'Connect');
    await m.openFreeformChannel(signalr);

    assert.equal(calls[0].url, '/bowire/api/channel/open?serverUrl=' + encodeURIComponent('http://localhost:5000'));
    assert.deepEqual(JSON.parse(calls[0].init.body), {
        service: '/chathub', method: 'Echo', protocol: 'signalr', metadata: { Authorization: 'Bearer x' }, methodType: 'Duplex',
    });
    assert.equal(opened[0].url, '/bowire/api/channel/c1/responses');
    assert.equal(m.freeformCall.state, 'open');
    assert.equal(m.freeformExecuteLabel(signalr), 'Send');

    await m.sendFreeformChannel('"hi"');
    assert.equal(calls[1].url, '/bowire/api/channel/c1/send');
    assert.deepEqual(JSON.parse(calls[1].init.body), { message: '"hi"' });

    opened[0].frame({ index: 0, data: 'hi' });
    assert.deepEqual(m.freeformCall.frames.map((f) => [f.dir, f.data]), [['send', '"hi"'], ['recv', 'hi']]);

    m.stopFreeformCall();
    assert.equal(opened[0].closed, true);
    assert.equal(calls[2].url, '/bowire/api/channel/c1/close');
    assert.equal(m.freeformCall.state, 'done');
});

test('a channel the plugin refuses ends as an error and says why', async () => {
    const { FakeEventSource, opened } = fakeEventSources();
    const { fn } = fakeFetch({
        '/api/channel/open': { ok: false, status: 400, body: { title: 'Channel not supported', detail: 'nats opens no channel' } },
    });
    const m = load({ EventSource: FakeEventSource, fetch: fn });

    await m.openFreeformChannel(signalr);

    assert.equal(m.freeformCall.state, 'error');
    assert.equal(m.freeformCall.error, 'nats opens no channel');
    assert.equal(opened.length, 0);
    assert.equal(m.toasts().length, 1);
});

test('REST keeps the unary label whatever method type is set', () => {
    const m = load({});
    assert.equal(m.freeformExecuteLabel({ protocol: 'rest', methodType: 'Duplex' }), 'Execute');
});

test('the frame list shows the state, the error and every frame', () => {
    const { FakeEventSource, opened } = fakeEventSources();
    const m = load({ EventSource: FakeEventSource });
    m.startFreeformStream(nats);
    opened[0].frame({ index: 0, data: { id: 1 } });

    const pane = m.renderFreeformFrames();

    assert.equal(pane.children[0].props.textContent, 'Receiving');
    assert.equal(pane.children.length, 2);
    assert.equal(pane.children[1].props.className, 'bowire-freeform-frame is-recv');
});
