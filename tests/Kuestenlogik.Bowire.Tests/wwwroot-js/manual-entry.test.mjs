// #995 — the hand-entry forms the protocol plugins describe.
//
// The workbench reads them from the ManualEntry rpc and, from them alone,
// offers each protocol's method types and assembles a call typed in by hand:
// service and method from the call kind's templates, metadata from the
// metadata fields. Nothing here may name a protocol of its own accord.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const appended = `
    var config = { prefix: '/bowire' };
    var protocols = opts.protocols || [];
    var fetch = opts.fetch;
    var _renders = 0;
    function render() { _renders++; }
    var rbLayouts = opts.rbLayouts || {};
    function registerRequestBuilderLayout(layout) { rbLayouts[layout.id] = layout; }
    var console = { warn: function () {} };
    return {
        get forms() { return manualEntryForms; },
        loadManualEntryForms: loadManualEntryForms,
        manualEntryMethodTypes: manualEntryMethodTypes,
        manualEntryFieldsFor: manualEntryFieldsFor,
        expandManualEntryTemplate: expandManualEntryTemplate,
        assembleManualEntryCall: assembleManualEntryCall,
        rbLayouts: rbLayouts,
        renders: function () { return _renders; }
    };
`;
const loadFragment = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/manual-entry.js', ['opts'], appended);
const load = (opts) => loadFragment({ opts: opts || {} });

// A form as the rpc transcodes it, in the shape of the Kafka plugin's.
const kafka = {
    protocol: 'kafka',
    urlPlaceholder: 'kafka://localhost:9092',
    callKinds: [
        { id: 'produce', label: 'Produce', methodType: 'Unary', service: '{topic}', method: 'produce', bodyExample: '{}' },
        { id: 'consume', label: 'Consume', methodType: 'ServerStreaming', service: '{topic}', method: 'consume' },
    ],
    fields: [
        { key: 'topic', label: 'Topic', placement: 'PLACEMENT_TEMPLATE', required: true },
        { key: 'key', label: 'Key', placement: 'PLACEMENT_METADATA', callKinds: ['produce'] },
        { key: 'partition', label: 'Partition', placement: 'PLACEMENT_METADATA', callKinds: ['produce'] },
    ],
};

const rest = { protocol: 'rest', callKinds: [{ id: 'Unary', label: 'Unary', methodType: 'Unary' }], fields: [] };

/** A fake fetch that serves `pages` in order and records the URLs. */
function pagedFetch(pages) {
    const urls = [];
    let i = 0;
    const fn = async (url) => {
        urls.push(url);
        const page = pages[i++];
        if (!page) return { ok: false, status: 404, json: async () => ({}) };
        return { ok: true, status: 200, json: async () => page };
    };
    return { fn, urls };
}

test('reads every page of the forms', async () => {
    const { fn, urls } = pagedFetch([
        { forms: [kafka], nextPageToken: 'kafka' },
        { forms: [rest], nextPageToken: '' },
    ]);
    const m = load({ fetch: fn, protocols: [{ id: 'kafka' }, { id: 'rest' }] });

    await m.loadManualEntryForms();

    assert.deepEqual(Object.keys(m.forms).sort(), ['kafka', 'rest']);
    assert.equal(urls[0], '/bowire/api/v1/manual-entry-forms?pageSize=200');
    assert.equal(urls[1], '/bowire/api/v1/manual-entry-forms?pageSize=200&pageToken=kafka');
    assert.ok(m.renders() > 0);
});

test('loads once per set of protocols', async () => {
    const { fn, urls } = pagedFetch([{ forms: [kafka] }, { forms: [kafka] }]);
    const m = load({ fetch: fn, protocols: [{ id: 'kafka' }] });

    await m.loadManualEntryForms();
    await m.loadManualEntryForms();

    assert.equal(urls.length, 1);
});

test('a host without the service leaves the forms empty and offers all four method types', async () => {
    const { fn } = pagedFetch([]);
    const m = load({ fetch: fn, protocols: [{ id: 'kafka' }] });

    await m.loadManualEntryForms();

    assert.deepEqual(m.forms, {});
    assert.deepEqual(m.manualEntryMethodTypes('kafka'), ['Unary', 'ServerStreaming', 'ClientStreaming', 'Duplex']);
});

test('the method types are the call kinds\' own, each once, in their order', async () => {
    const nats = {
        protocol: 'nats',
        callKinds: [
            { id: 'publish', methodType: 'Unary' },
            { id: 'request', methodType: 'Unary' },
            { id: 'subscribe', methodType: 'ServerStreaming' },
        ],
        fields: [],
    };
    const { fn } = pagedFetch([{ forms: [nats] }]);
    const m = load({ fetch: fn, protocols: [{ id: 'nats' }] });
    await m.loadManualEntryForms();

    assert.deepEqual(m.manualEntryMethodTypes('nats'), ['Unary', 'ServerStreaming']);
});

test('a template takes the values of its fields; a missing one is empty', () => {
    const m = load();
    assert.equal(m.expandManualEntryTemplate('{subject}/request', { subject: 'orders.new' }), 'orders.new/request');
    assert.equal(m.expandManualEntryTemplate('nats/jetstream/{stream}/publish/{subject}', { stream: 'S' }), 'nats/jetstream/S/publish/');
});

test('a produce puts the topic into the service and key and partition into the metadata', () => {
    const m = load();
    const call = m.assembleManualEntryCall(kafka, 'produce', { topic: 'orders', key: 'k1', partition: '' }, { trace: 't' });

    assert.equal(call.service, 'orders');
    assert.equal(call.method, 'produce');
    assert.equal(call.methodType, 'Unary');
    assert.deepEqual(call.metadata, { trace: 't', key: 'k1' });
    assert.deepEqual(call.missing, []);
});

test('a field of another call kind stays out of the call', () => {
    const m = load();
    const call = m.assembleManualEntryCall(kafka, 'consume', { topic: 'orders', key: 'k1' }, {});

    assert.equal(call.methodType, 'ServerStreaming');
    assert.deepEqual(call.metadata, {});
    assert.deepEqual(m.manualEntryFieldsFor(kafka, 'consume').map((f) => f.key), ['topic']);
});

test('a required field left empty is reported by its label', () => {
    const m = load();
    const call = m.assembleManualEntryCall(kafka, 'produce', { topic: '  ' }, {});

    assert.deepEqual(call.missing, ['Topic']);
});

test('a call kind without a service falls back to the protocol id', () => {
    const m = load();
    const form = { protocol: 'odd', callKinds: [{ id: 'x', methodType: 'Unary', service: '', method: 'go' }], fields: [] };

    assert.equal(m.assembleManualEntryCall(form, 'x', {}, {}).service, 'odd');
});

test('a generic layout is registered for a form with fields, never over a shipped one', async () => {
    const shipped = { id: 'mqtt', label: 'MQTT' };
    const mqtt = { protocol: 'mqtt', callKinds: [{ id: 'Duplex', methodType: 'Duplex' }], fields: [{ key: 'topic', placement: 'PLACEMENT_TEMPLATE' }] };
    const { fn } = pagedFetch([{ forms: [kafka, rest, mqtt] }]);
    const m = load({ fetch: fn, protocols: [{ id: 'kafka', name: 'Kafka' }], rbLayouts: { mqtt: shipped } });

    await m.loadManualEntryForms();

    assert.equal(m.rbLayouts.kafka.manualEntry, true);
    assert.equal(m.rbLayouts.kafka.label, 'Kafka');
    assert.equal(m.rbLayouts.kafka.urlPlaceholder, 'kafka://localhost:9092');
    assert.equal(m.rbLayouts.rest, undefined, 'a form without fields gets no layout');
    assert.equal(m.rbLayouts.mqtt, shipped, 'the shipped MQTT layout stays');
    assert.deepEqual(m.rbLayouts.kafka.defaults(), {
        kind: 'produce', values: { topic: '', key: '', partition: '' }, body: '{}', metadata: [],
    });
});
