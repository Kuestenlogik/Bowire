// #801 — mcp-tasks-model.js unit tests.
//
// The pure half of the MCP tasks surface: recognising the plugin's frames,
// folding a stream into the task it reports on, turning an elicitation's
// requestedSchema into form fields and the typed values back into content,
// the Tasks/update body, and the per-workspace list of tasks still running.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const load = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/mcp-tasks-model.js',
    [],
    `
    return {
        mcpParseFrame, mcpTaskSummary, mcpIsTerminalStatus, mcpStatusClass,
        mcpExpiresAt, mcpFormatDuration, mcpInputRequestList, mcpElicitFields,
        mcpFieldInitial, mcpBuildElicitContent, mcpBuildUpdateBody,
        mcpPendingEntry, mcpUpsertPending, mcpRemovePending, mcpPendingExpired,
        mcpPrunePending, mcpPendingFor,
    };
    `
);
const m = load();

// A stream message as invokeStreaming stores it: the SSE envelope with the
// plugin's frame as a JSON string in `data`.
const frame = (event, data, index = 0) => ({ index, data: JSON.stringify({ event, data }) });
const task = (status, extra = {}) => frame('task', {
    shape: '2026-07-28', taskId: 't-1', status, via: 'poll',
    createdAt: '2026-10-20T10:00:00.000Z', ttlMs: 600000, pollIntervalMs: 500, ...extra,
});

// ---- frame recognition ----

test('mcpParseFrame reads the five frame kinds the plugin emits', () => {
    for (const kind of ['progress', 'log', 'result', 'error']) {
        assert.deepEqual(m.mcpParseFrame(frame(kind, { x: 1 })), { event: kind, data: { x: 1 } });
    }
    const t = m.mcpParseFrame(task('working'));
    assert.equal(t.event, 'task');
    assert.equal(t.data.status, 'working');
});

test('mcpParseFrame accepts an already-parsed frame object', () => {
    assert.equal(m.mcpParseFrame({ data: { event: 'log', data: 'hi' } }).event, 'log');
});

test('mcpParseFrame leaves other payloads alone', () => {
    assert.equal(m.mcpParseFrame(null), null);
    assert.equal(m.mcpParseFrame({ data: 'plain text' }), null);
    assert.equal(m.mcpParseFrame({ data: '{not json' }), null);
    assert.equal(m.mcpParseFrame({ data: '[1,2]' }), null);
    // Another protocol's event with an unknown kind.
    assert.equal(m.mcpParseFrame({ data: JSON.stringify({ event: 'tick', data: 1 }) }), null);
    // The right kind but more keys than the plugin's envelope has.
    assert.equal(m.mcpParseFrame({ data: JSON.stringify({ event: 'log', data: 1, id: 3 }) }), null);
    // A task frame without the fields that make it one.
    assert.equal(m.mcpParseFrame({ data: JSON.stringify({ event: 'task', data: { status: 'working' } }) }), null);
});

test('mcpStatusClass maps known statuses and keeps unknown ones neutral', () => {
    assert.equal(m.mcpStatusClass('input_required'), 'input-required');
    assert.equal(m.mcpStatusClass('completed'), 'completed');
    assert.equal(m.mcpStatusClass('paused'), 'unknown');
    assert.equal(m.mcpIsTerminalStatus('cancelled'), true);
    assert.equal(m.mcpIsTerminalStatus('working'), false);
});

// ---- folding a stream ----

test('mcpTaskSummary is null for an ordinary tool call', () => {
    assert.equal(m.mcpTaskSummary([frame('progress', {}), frame('result', { content: [] })]), null);
    assert.equal(m.mcpTaskSummary(undefined), null);
});

test('mcpTaskSummary: the latest task frame wins, and a result ends it as completed', () => {
    const s1 = m.mcpTaskSummary([task('working', { via: 'create' }), task('input_required')]);
    assert.equal(s1.task.status, 'input_required');
    assert.equal(s1.terminal, false);
    assert.equal(s1.updates, 2);

    const s2 = m.mcpTaskSummary([task('working'), task('completed'), frame('result', { content: [{ type: 'text', text: 'ok' }] })]);
    assert.equal(s2.terminal, true);
    assert.equal(s2.outcome, 'completed');
    assert.equal(s2.result.content[0].text, 'ok');
    assert.equal(s2.resultIndex, 2);
});

test('mcpTaskSummary: an error frame (TTL elapsed, purged) ends a non-terminal task as failed', () => {
    const s = m.mcpTaskSummary([task('working'), frame('error', { message: 'past its TTL', taskId: 't-1' })]);
    assert.equal(s.terminal, true);
    assert.equal(s.outcome, 'failed');
    assert.equal(s.error.message, 'past its TTL');
});

test('mcpTaskSummary: a cancelled task frame is terminal with no result', () => {
    const s = m.mcpTaskSummary([task('working'), task('cancelled')]);
    assert.equal(s.terminal, true);
    assert.equal(s.outcome, 'cancelled');
    assert.equal(s.result, null);
});

test('mcpTaskSummary: a second task on the same stream replaces the first', () => {
    const s = m.mcpTaskSummary([task('completed'), frame('task', { taskId: 't-2', status: 'working' })]);
    assert.equal(s.task.taskId, 't-2');
    assert.equal(s.terminal, false);
});

// ---- time ----

test('mcpExpiresAt counts from createdAt; no TTL means none', () => {
    assert.equal(m.mcpExpiresAt('2026-10-20T10:00:00.000Z', 60000), Date.parse('2026-10-20T10:01:00.000Z'));
    assert.equal(m.mcpExpiresAt('2026-10-20T10:00:00.000Z', null), null);
    assert.equal(m.mcpExpiresAt(null, 60000), null);
    assert.equal(m.mcpExpiresAt('not a date', 60000), null);
});

test('mcpFormatDuration scales its units', () => {
    assert.equal(m.mcpFormatDuration(45_000), '45s');
    assert.equal(m.mcpFormatDuration(185_000), '3m 05s');
    assert.equal(m.mcpFormatDuration(2 * 3600_000 + 3 * 60_000), '2h 03m');
    assert.equal(m.mcpFormatDuration(4 * 86400_000 + 2 * 3600_000), '4d 2h');
    assert.equal(m.mcpFormatDuration(-5), '0s');
    assert.equal(m.mcpFormatDuration(undefined), '');
});

// ---- input requests ----

const elicit = (message, requestedSchema) => ({
    method: 'elicitation/create', params: { mode: 'form', message, requestedSchema },
});

test('mcpInputRequestList orders by key, classifies, and marks answered keys', () => {
    const list = m.mcpInputRequestList({
        b: elicit('Your name?', { type: 'object', properties: {} }),
        a: { method: 'sampling/createMessage', params: { messages: [] } },
        c: { method: 'roots/list', params: {} },
    }, { b: 'accept' });
    assert.deepEqual(list.map((r) => [r.key, r.kind, r.answered]),
        [['a', 'sampling', false], ['b', 'elicitation', true], ['c', 'other', false]]);
    assert.equal(list[1].message, 'Your name?');
    assert.equal(list[1].mode, 'form');
    assert.deepEqual(m.mcpInputRequestList(null), []);
});

test('mcpElicitFields maps every primitive the elicitation spec allows', () => {
    const fields = m.mcpElicitFields({
        type: 'object',
        properties: {
            name: { type: 'string', title: 'Name', description: 'Who you are', minLength: 2 },
            email: { type: 'string', format: 'email' },
            colour: { type: 'string', enum: ['r', 'g'], enumNames: ['Red', 'Green'] },
            size: { type: 'string', oneOf: [{ const: 's', title: 'Small' }, { const: 'l', title: 'Large' }] },
            qty: { type: 'integer', minimum: 1, maximum: 40, default: 1 },
            ratio: { type: 'number' },
            ok: { type: 'boolean', default: true },
            tags: { type: 'array', items: { anyOf: [{ const: 'x', title: 'X' }, { const: 'y', title: 'Y' }] }, maxItems: 2 },
            plain: { type: 'array', items: { type: 'string', enum: ['p', 'q'] } },
            nested: { type: 'object' },
        },
        required: ['name', 'qty'],
    });
    const byKey = Object.fromEntries(fields.map((f) => [f.key, f]));
    assert.equal(byKey.name.kind, 'text');
    assert.equal(byKey.name.label, 'Name');
    assert.equal(byKey.name.required, true);
    assert.equal(byKey.name.min, 2);
    assert.equal(byKey.email.inputType, 'email');
    assert.deepEqual(byKey.colour.options, [{ value: 'r', label: 'Red' }, { value: 'g', label: 'Green' }]);
    assert.equal(byKey.size.kind, 'select');
    assert.deepEqual(byKey.size.options.map((o) => o.label), ['Small', 'Large']);
    assert.equal(byKey.qty.kind, 'integer');
    assert.equal(byKey.qty.min, 1);
    assert.equal(byKey.qty.max, 40);
    assert.equal(byKey.ratio.kind, 'number');
    assert.equal(byKey.ok.kind, 'checkbox');
    assert.equal(byKey.tags.kind, 'multiselect');
    assert.equal(byKey.tags.max, 2);
    assert.deepEqual(byKey.plain.options.map((o) => o.value), ['p', 'q']);
    assert.equal(byKey.nested.kind, 'unsupported', 'shown as unsupported, not dropped');
    assert.equal(byKey.ratio.label, 'ratio', 'no title falls back to the key');
});

test('mcpElicitFields copes with a missing or empty schema', () => {
    assert.deepEqual(m.mcpElicitFields(null), []);
    assert.deepEqual(m.mcpElicitFields({ type: 'object' }), []);
});

test('mcpFieldInitial: a draft beats the default, which beats empty', () => {
    const [qty, ok, tags] = m.mcpElicitFields({
        type: 'object',
        properties: {
            qty: { type: 'integer', default: 3 },
            ok: { type: 'boolean' },
            tags: { type: 'array', items: { enum: ['a'] }, default: ['a'] },
        },
    });
    assert.equal(m.mcpFieldInitial(qty, undefined), '3');
    assert.equal(m.mcpFieldInitial(qty, '7'), '7');
    assert.equal(m.mcpFieldInitial(ok, undefined), false);
    assert.deepEqual(m.mcpFieldInitial(tags, undefined), ['a']);
});

test('mcpBuildElicitContent types the values and leaves empty optional fields out', () => {
    const fields = m.mcpElicitFields({
        type: 'object',
        properties: {
            confirm: { type: 'boolean' },
            quantity: { type: 'integer', minimum: 1, maximum: 40 },
            carrier: { type: 'string', enum: ['road', 'rail'] },
            note: { type: 'string' },
            tags: { type: 'array', items: { enum: ['a', 'b'] } },
        },
        required: ['confirm', 'quantity', 'carrier'],
    });
    const { content, errors } = m.mcpBuildElicitContent(fields,
        { confirm: true, quantity: '12', carrier: 'rail', note: '  ', tags: ['b'] });
    assert.deepEqual(errors, {});
    assert.deepEqual(content, { confirm: true, quantity: 12, carrier: 'rail', tags: ['b'] });
});

test('mcpBuildElicitContent reports what the schema rejects, per field', () => {
    const fields = m.mcpElicitFields({
        type: 'object',
        properties: {
            quantity: { type: 'integer', minimum: 1, maximum: 40 },
            ratio: { type: 'number' },
            carrier: { type: 'string', enum: ['road'] },
            name: { type: 'string', maxLength: 3 },
            picks: { type: 'array', items: { enum: ['a', 'b'] }, minItems: 2 },
            needed: { type: 'string' },
        },
        required: ['needed'],
    });
    const errorsFor = (values) => m.mcpBuildElicitContent(fields, values).errors;
    assert.deepEqual(errorsFor({ quantity: '2.5', ratio: 'x', carrier: 'sea', name: 'abcd', picks: ['a'] }),
        { quantity: 'integer', ratio: 'number', carrier: 'option', name: 'max', picks: 'min', needed: 'required' });
    assert.equal(errorsFor({ quantity: '0', needed: 'y' }).quantity, 'min');
    assert.equal(errorsFor({ quantity: '41', needed: 'y' }).quantity, 'max');
    // An unticked checkbox is an answer (false), never "missing".
    const [flag] = m.mcpElicitFields({ type: 'object', properties: { flag: { type: 'boolean' } }, required: ['flag'] });
    assert.deepEqual(m.mcpBuildElicitContent([flag], {}), { content: { flag: false }, errors: {} });
});

test('mcpBuildUpdateBody: accept carries content, decline does not', () => {
    assert.deepEqual(m.mcpBuildUpdateBody('t-1', 'k1', 'accept', { name: 'Ada' }),
        { taskId: 't-1', inputResponses: { k1: { action: 'accept', content: { name: 'Ada' } } } });
    assert.deepEqual(m.mcpBuildUpdateBody('t-1', 'k1', 'decline', { ignored: true }),
        { taskId: 't-1', inputResponses: { k1: { action: 'decline' } } });
    assert.deepEqual(m.mcpBuildUpdateBody('t-1', 'k1', 'accept'),
        { taskId: 't-1', inputResponses: { k1: { action: 'accept', content: {} } } });
});

// ---- the pending list ----

const SERVER = 'http://localhost:5190/mcp';
const NOW = Date.parse('2026-10-20T10:05:00.000Z');
const data = (status, extra = {}) => ({
    shape: '2026-07-28', taskId: 't-1', status,
    createdAt: '2026-10-20T10:00:00.000Z', ttlMs: 600000, ...extra,
});

test('mcpPendingEntry keeps what a reload needs to follow the task', () => {
    assert.deepEqual(m.mcpPendingEntry(data('working'), { serverUrl: SERVER, tool: 'generate_report' }, NOW), {
        serverUrl: SERVER, tool: 'generate_report', taskId: 't-1', shape: '2026-07-28', status: 'working',
        createdAt: '2026-10-20T10:00:00.000Z', ttlMs: 600000, savedAt: NOW,
    });
    assert.equal(m.mcpPendingEntry(data('working', { ttlMs: null }), { serverUrl: SERVER }, NOW).ttlMs, null);
});

test('mcpUpsertPending adds a running task once, and a terminal one leaves the list', () => {
    const input = [];
    let list = m.mcpUpsertPending(input, m.mcpPendingEntry(data('working'), { serverUrl: SERVER, tool: 'gen' }, NOW));
    assert.equal(input.length, 0, 'the input list is not mutated');
    list = m.mcpUpsertPending(list, m.mcpPendingEntry(data('input_required'), { serverUrl: SERVER, tool: 'gen' }, NOW));
    assert.equal(list.length, 1);
    assert.equal(list[0].status, 'input_required');
    list = m.mcpUpsertPending(list, m.mcpPendingEntry(data('completed'), { serverUrl: SERVER, tool: 'gen' }, NOW));
    assert.deepEqual(list, []);
});

test('mcpUpsertPending keeps the tool when Tasks/follow reports the task without one', () => {
    let list = m.mcpUpsertPending([], m.mcpPendingEntry(data('working'), { serverUrl: SERVER, tool: 'gen' }, NOW));
    list = m.mcpUpsertPending(list, m.mcpPendingEntry(data('working', { createdAt: undefined }), { serverUrl: SERVER, tool: null }, NOW + 1));
    assert.equal(list[0].tool, 'gen');
    assert.equal(list[0].createdAt, '2026-10-20T10:00:00.000Z');
});

test('the same task id on another server is another entry', () => {
    let list = m.mcpUpsertPending([], m.mcpPendingEntry(data('working'), { serverUrl: SERVER, tool: 'gen' }, NOW));
    list = m.mcpUpsertPending(list, m.mcpPendingEntry(data('working'), { serverUrl: 'http://other/mcp', tool: 'gen' }, NOW));
    assert.equal(list.length, 2);
    assert.equal(m.mcpRemovePending(list, SERVER, 't-1').length, 1);
});

test('mcpPendingExpired: past createdAt + ttlMs; unlimited never', () => {
    const entry = m.mcpPendingEntry(data('working', { ttlMs: 60000 }), { serverUrl: SERVER }, NOW);
    assert.equal(m.mcpPendingExpired(entry, Date.parse('2026-10-20T10:00:59.000Z')), false);
    assert.equal(m.mcpPendingExpired(entry, Date.parse('2026-10-20T10:01:00.000Z')), true);
    const unlimited = m.mcpPendingEntry(data('working', { ttlMs: null }), { serverUrl: SERVER }, NOW);
    assert.equal(m.mcpPendingExpired(unlimited, NOW + 365 * 86400_000), false);
});

test('mcpPrunePending drops junk and entries expired more than a day ago', () => {
    const fresh = m.mcpPendingEntry(data('working'), { serverUrl: SERVER }, NOW);
    const justExpired = m.mcpPendingEntry(data('working', { taskId: 't-2', ttlMs: 1000 }), { serverUrl: SERVER }, NOW);
    const longGone = m.mcpPendingEntry(data('working', { taskId: 't-3', createdAt: '2026-10-18T10:00:00.000Z', ttlMs: 1000 }), { serverUrl: SERVER }, NOW);
    const kept = m.mcpPrunePending([fresh, justExpired, longGone, null, { taskId: 5 }, 'x'], NOW);
    assert.deepEqual(kept.map((e) => e.taskId), ['t-1', 't-2']);
    assert.deepEqual(m.mcpPrunePending('not a list', NOW), []);
});

test('mcpPendingFor filters by server and tool, newest first, and marks expiry', () => {
    let list = [];
    list = m.mcpUpsertPending(list, m.mcpPendingEntry(data('working', { taskId: 'a' }), { serverUrl: SERVER, tool: 'gen' }, NOW - 2));
    list = m.mcpUpsertPending(list, m.mcpPendingEntry(data('working', { taskId: 'b', ttlMs: 1000 }), { serverUrl: SERVER, tool: 'gen' }, NOW - 1));
    list = m.mcpUpsertPending(list, m.mcpPendingEntry(data('working', { taskId: 'c' }), { serverUrl: SERVER, tool: 'ship' }, NOW));
    list = m.mcpUpsertPending(list, m.mcpPendingEntry(data('working', { taskId: 'd' }), { serverUrl: 'http://other/mcp', tool: 'gen' }, NOW));

    const forGen = m.mcpPendingFor(list, SERVER, 'gen', NOW);
    assert.deepEqual(forGen.map((e) => [e.taskId, e.expired]), [['b', true], ['a', false]]);
    // Tasks/follow passes no tool: every task on the server.
    assert.deepEqual(m.mcpPendingFor(list, SERVER, null, NOW).map((e) => e.taskId), ['c', 'b', 'a']);
});
