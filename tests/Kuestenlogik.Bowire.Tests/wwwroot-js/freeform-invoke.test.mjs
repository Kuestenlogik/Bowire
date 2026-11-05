// #996 — the body the classic freeform form posts to /api/invoke.
//
// Metadata used to go out for ad-hoc REST only, so a SOAP call entered
// by hand lost the soap_action and target_namespace it is addressed by.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const load = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/freeform-invoke.js',
    [],
    '\nreturn { buildFreeformInvokeBody: buildFreeformInvokeBody };\n',
);
const { buildFreeformInvokeBody } = load();

test('SOAP by hand: the metadata carrying action and namespace is sent', () => {
    const body = buildFreeformInvokeBody({
        protocol: 'soap',
        service: 'Calculator',
        method: 'Add',
        metadata: { soap_action: 'urn:calc/Add', target_namespace: 'urn:calc' },
    }, '<Add><a>1</a></Add>', true);

    assert.deepEqual(body.metadata, { soap_action: 'urn:calc/Add', target_namespace: 'urn:calc' });
    assert.equal(body.protocol, 'soap');
    assert.deepEqual(body.messages, ['<Add><a>1</a></Add>']);
});

test('every protocol gets its metadata, not only REST', () => {
    for (const protocol of ['grpc', 'signalr', 'kafka', 'nats', 'rest']) {
        const body = buildFreeformInvokeBody({ protocol, service: 's', method: 'm', metadata: { k: 'v' } }, '{}', true);
        assert.deepEqual(body.metadata, { k: 'v' }, protocol);
    }
});

test('no metadata, or an empty map, is sent as null', () => {
    assert.equal(buildFreeformInvokeBody({ protocol: 'soap' }, '{}', true).metadata, null);
    assert.equal(buildFreeformInvokeBody({ protocol: 'soap', metadata: {} }, '{}', true).metadata, null);
});

test('a REST verb without a body sends the {} placeholder', () => {
    const body = buildFreeformInvokeBody({ protocol: 'rest', method: 'GET' }, '{"x":1}', false);
    assert.deepEqual(body.messages, ['{}']);
});
