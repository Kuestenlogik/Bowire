// #128 — the hub's executor picker in the Parallel sessions dialog.
//
// hub.js offers the live agents tagged `parallel-executor` as executors and
// adds a picked one to the comma-separated Hosts field. The URLs come from
// other machines, so only http(s) ones may become hosts or links.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const _host = `
        var config = { prefix: '/bowire' };
        return {
            executors: hubExecutorsFrom,
            append: hubAppendHost,
            safe: _hubSafeUrl
        };
`;

const load = compileFragment('../../../src/Kuestenlogik.Bowire/wwwroot/js/hub.js', [], _host);

function agent(over) {
    return Object.assign({
        serviceName: 'orders', instanceId: 'node-1', callbackUrl: 'https://node-1:5443/bowire',
        tags: ['parallel-executor'], live: true
    }, over);
}

test('only live agents tagged parallel-executor are offered', () => {
    const f = load();
    const picked = f.executors([
        agent({ instanceId: 'a' }),
        agent({ instanceId: 'b', live: false }),
        agent({ instanceId: 'c', tags: ['env:prod'] }),
        agent({ instanceId: 'd', tags: undefined }),
        agent({ instanceId: 'e', callbackUrl: 'javascript:alert(1)' }),
        null
    ]);
    assert.deepEqual(picked.map((a) => a.instanceId), ['a']);
    assert.deepEqual(f.executors(undefined), []);
});

test('a picked executor is added to the hosts once', () => {
    const f = load();
    assert.equal(f.append('', 'https://a/bowire'), 'https://a/bowire');
    assert.equal(f.append('https://a/bowire', 'https://b/bowire'), 'https://a/bowire, https://b/bowire');
    assert.equal(f.append(' https://A/bowire/ ,', 'https://a/bowire'), 'https://A/bowire/');
});

test('only http and https callback URLs count', () => {
    const f = load();
    assert.equal(f.safe('http://x'), 'http://x');
    assert.equal(f.safe('HTTPS://x'), 'HTTPS://x');
    assert.equal(f.safe('javascript:alert(1)'), null);
    assert.equal(f.safe('//x'), null);
    assert.equal(f.safe(42), null);
});
