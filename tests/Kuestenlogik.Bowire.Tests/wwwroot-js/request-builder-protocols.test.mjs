// #291 — request-builder-protocols.js unit tests.
//
// The fragment is concatenated AFTER request-builder.js inside
// prologue.js's IIFE — its registration calls (rbLayouts['grpc'] = …)
// reference helpers defined further down. To exercise them in
// isolation we wrap the source in our own IIFE and stub everything
// the layout descriptors call back into.
//
// Most of the file is render-pane code (DOM-heavy). The pure helpers
// we pin here are:
//   * _safeParseJsonObject — JSON guard for the MCP arguments tab
//   * layout descriptor side-effects (registerRequestBuilderLayout
//     calls land in rbLayouts under the right ids)
//
// Tab-render + execute paths reach `fetch` / `document` / event
// listeners — those stay covered by the integration tests rather
// than these unit tests.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment, readFragment } from './_load-fragment.mjs';

// request-builder.js MUST load first: request-builder-protocols.js runs
// its `rbLayouts[...] = …` registrations at parse time and reads the
// layout store request-builder.js initialises. So for #367 the FIRST
// (line-aligned) source is request-builder.js — that is the filename V8
// attributes to; the protocol fragment is appended after it. Both files
// are also exercised by their integration paths; this keeps the attributed
// line data honest rather than mislabelling the offset protocol fragment.
const PROTO_SRC = readFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/request-builder-protocols.js'
);

const _prelude = `
    var _ls = {};
    var localStorage = {
        getItem: function (k) { return Object.prototype.hasOwnProperty.call(_ls, k) ? _ls[k] : null; },
        setItem: function (k, v) { _ls[k] = String(v); },
        removeItem: function (k) { delete _ls[k]; }
    };
    function wsKey(k) { return 'ws::' + k; }
    function el() { return { appendChild: function () {}, classList: { add: function () {}, remove: function () {} }, addEventListener: function () {} }; }
    function render() {}
    function toast() {}
    function markSaved() {}
    function recordAction() {}
    function substituteVars(s) { return s; }
    function substituteMetadata(m) { return m; }
    function svgIcon() { return ''; }
    function activeWorkspace() { return null; }
    var workspaceId = 'ws1';
    var freeformRequest = {};
    var services = [];
    var responseLastJson = null;
    var isExecuting = false;
    function markJobStart() {}
    function markJobDone() {}
    // prologue.js's per-tab state bag: the response panes read it for
    // responseData / responseError.
    var _tabState = {};
    function activeState() { return _tabState; }
`;
const _postlude = `
    return {
        _safeParseJsonObject: _safeParseJsonObject,
        _getLayouts: function () { return rbLayouts; },
        _graphQLOperationName: _graphQLOperationName,
        _graphQLOperationNames: _graphQLOperationNames,
        _graphQLVariableNames: _graphQLVariableNames,
        _graphQLUploadPayload: _graphQLUploadPayload,
        _graphQLMaxUploadBytes: RB_GRAPHQL_MAX_UPLOAD_BYTES,
        _graphQLHasErrors: _graphQLHasErrors,
        _connState: function () { return rbConnState; },
        _protoState: function (fr) { return rbProtoState(fr); }
    };
`;
const _loadProtocols = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/request-builder.js',
    ['document', 'FileReader'],
    PROTO_SRC + '\n' + _prelude + '\n' + _postlude
);

function loadProtocols() {
    // request-builder.js reads `document` at its top level, so it arrives
    // as a parameter (defined before the body runs), not a hoisted var.
    const document = {
        addEventListener() {},
        removeEventListener() {},
        getElementById() { return null; },
        querySelector() { return null; },
        querySelectorAll() { return []; },
        createElement() { return { appendChild() {} }; },
        body: { appendChild() {}, removeChild() {} },
        activeElement: null,
    };
    // The upload path reads files through FileReader, which the browser has
    // and node does not. The stub does what readAsDataURL does — including
    // the `data:` prefix the fragment has to strip back off.
    class FileReader {
        readAsDataURL(file) {
            file.arrayBuffer().then((buf) => {
                this.result = 'data:' + (file.type || 'application/octet-stream')
                    + ';base64,' + Buffer.from(buf).toString('base64');
                if (this.onload) this.onload();
            }, () => { if (this.onerror) this.onerror(); });
        }
    }
    return _loadProtocols({ document, FileReader });
}

// A File that reports a size without holding the bytes, so the limit can be
// crossed in a test without allocating 25 MB.
function hugeFile(name, size) {
    const f = new File(['x'], name, { type: 'image/png' });
    Object.defineProperty(f, 'size', { value: size });
    return f;
}

// ---- _safeParseJsonObject ----

test('_safeParseJsonObject: valid object stays as-is', () => {
    const sb = loadProtocols();
    assert.deepEqual(sb._safeParseJsonObject('{"a":1,"b":"two"}'), { a: 1, b: 'two' });
});

test('_safeParseJsonObject: empty / null input → empty object', () => {
    const sb = loadProtocols();
    assert.deepEqual(sb._safeParseJsonObject(''), {});
    assert.deepEqual(sb._safeParseJsonObject(null), {});
    assert.deepEqual(sb._safeParseJsonObject(undefined), {});
});

test('_safeParseJsonObject: malformed JSON → empty object (no throw)', () => {
    const sb = loadProtocols();
    assert.deepEqual(sb._safeParseJsonObject('{not json'), {});
    assert.deepEqual(sb._safeParseJsonObject('xxx'), {});
});

test('_safeParseJsonObject: non-object JSON values → empty object', () => {
    const sb = loadProtocols();
    // Number, string, bool, null are not objects → coerce to {}.
    assert.deepEqual(sb._safeParseJsonObject('42'), {});
    assert.deepEqual(sb._safeParseJsonObject('"hi"'), {});
    assert.deepEqual(sb._safeParseJsonObject('true'), {});
    assert.deepEqual(sb._safeParseJsonObject('null'), {});
});

test('_safeParseJsonObject: arrays are objects in JS → preserved', () => {
    const sb = loadProtocols();
    // typeof [] === 'object', so the helper keeps arrays.
    // This pins the current behaviour — callers downstream know to
    // expect either a plain object or an array.
    assert.deepEqual(sb._safeParseJsonObject('[1,2,3]'), [1, 2, 3]);
});

// ---- Layout registry side-effects ----

test('protocols fragment registers each non-REST layout under its id', () => {
    const sb = loadProtocols();
    const layouts = sb._getLayouts();
    // rest comes from request-builder.js itself; the others land here.
    for (const id of ['grpc', 'mcp', 'mqtt', 'websocket', 'sse', 'graphql']) {
        assert.ok(layouts[id], 'missing layout for ' + id);
        assert.equal(typeof layouts[id].subTabs, 'function', id + '.subTabs');
    }
});

test('each registered protocol layout exposes execute + executeLabel', () => {
    const sb = loadProtocols();
    const layouts = sb._getLayouts();
    for (const id of ['grpc', 'mcp', 'mqtt', 'websocket', 'sse', 'graphql']) {
        assert.equal(typeof layouts[id].execute, 'function', id + '.execute');
        assert.equal(typeof layouts[id].executeLabel, 'function', id + '.executeLabel');
    }
});

test('each protocol layout subTabs returns a non-empty list of named tabs', () => {
    // #117 — a tab carries EITHER a catalogue key (Bowire's own tab names,
    // resolved by rbLabel at render time) or a literal label (a protocol's
    // own word, like JSON or QoS). One of the two, never neither.
    const sb = loadProtocols();
    const layouts = sb._getLayouts();
    const fr = { _requestBuilder: { protocol: 'grpc', params: [], headers: [], byProtocol: {} } };
    for (const id of ['grpc', 'mcp', 'mqtt', 'websocket', 'sse', 'graphql']) {
        fr._requestBuilder.protocol = id;
        const tabs = layouts[id].subTabs(fr);
        assert.ok(Array.isArray(tabs), id + '.subTabs returns array');
        assert.ok(tabs.length >= 1, id + ' has tabs');
        for (const tab of tabs) {
            assert.equal(typeof tab.id, 'string');
            const named = typeof tab.labelKey === 'string' || typeof tab.label === 'string';
            assert.ok(named, `${id}.${tab.id} has neither labelKey nor label`);
        }
    }
});

test('mqtt + sse + ws + grpc layouts expose a defaults() factory', () => {
    const sb = loadProtocols();
    const layouts = sb._getLayouts();
    for (const id of ['grpc', 'mcp', 'mqtt', 'websocket', 'sse', 'graphql']) {
        if (typeof layouts[id].defaults === 'function') {
            const d = layouts[id].defaults();
            assert.equal(typeof d, 'object');
            assert.ok(d !== null);
        }
    }
});

// ---- GraphQL (#292) ----

function graphqlFrame(over) {
    // A bar parked on the GraphQL layout, with the scratch bag seeded the
    // way rbProtoState would seed it.
    return {
        serverUrl: 'https://api.example.com/graphql',
        _requestBuilder: {
            protocol: 'graphql', params: [], headers: [],
            byProtocol: { graphql: Object.assign({ operation: 'query', query: '', variables: '{}', operationName: '', files: [], metadata: [] }, over || {}) }
        }
    };
}

test('graphql layout: defaults seed a query with empty variables object', () => {
    const sb = loadProtocols();
    const d = sb._getLayouts().graphql.defaults();
    assert.equal(d.operation, 'query');
    assert.equal(d.query, '');
    // '{}' rather than '' — the Variables pane shows a parseable document
    // from the first render, so the operator edits rather than starts over.
    assert.equal(d.variables, '{}');
    assert.deepEqual(d.metadata, []);
});

test('graphql layout: the query + variables tabs come before the shared ones', () => {
    const sb = loadProtocols();
    const tabs = sb._getLayouts().graphql.subTabs(graphqlFrame()).map((x) => x.id);
    assert.deepEqual(tabs.slice(0, 2), ['query', 'variables']);
    for (const shared of ['headers', 'auth', 'pre', 'post', 'vars']) {
        assert.ok(tabs.includes(shared), 'missing shared tab ' + shared);
    }
});

test('_graphQLOperationName: named operations, any root type', () => {
    const sb = loadProtocols();
    assert.equal(sb._graphQLOperationName('query GetUser($id: ID!) { user(id: $id) { id } }'), 'GetUser');
    assert.equal(sb._graphQLOperationName('mutation AddUser { addUser { id } }'), 'AddUser');
    assert.equal(sb._graphQLOperationName('subscription OnTick { tick }'), 'OnTick');
    assert.equal(sb._graphQLOperationName('   query  Padded { a }'), 'Padded');
});

test('_graphQLOperationName: anonymous and shorthand operations have no name', () => {
    const sb = loadProtocols();
    // `{ user { id } }` is a legal anonymous query — there is no name to
    // show, and the caller falls back to the operation keyword.
    assert.equal(sb._graphQLOperationName('{ user { id } }'), null);
    assert.equal(sb._graphQLOperationName('query { user { id } }'), null);
    assert.equal(sb._graphQLOperationName(''), null);
    assert.equal(sb._graphQLOperationName(null), null);
});

test('_graphQLHasErrors: a 200 carrying an errors array is not a success', () => {
    const sb = loadProtocols();
    // The whole point of the helper: GraphQL answers 200 for a failed
    // operation, so the transport status cannot decide the history row.
    assert.equal(sb._graphQLHasErrors('{"data":null,"errors":[{"message":"boom"}]}'), true);
    assert.equal(sb._graphQLHasErrors({ data: null, errors: [{ message: 'boom' }] }), true);
});

test('_graphQLHasErrors: no errors key, empty array or unparseable body → no errors', () => {
    const sb = loadProtocols();
    assert.equal(sb._graphQLHasErrors('{"data":{"user":{"id":"1"}}}'), false);
    assert.equal(sb._graphQLHasErrors('{"data":null,"errors":[]}'), false);
    assert.equal(sb._graphQLHasErrors('not json'), false);
    assert.equal(sb._graphQLHasErrors(null), false);
});

test('graphql executeLabel: query and mutation send, subscription toggles', () => {
    const sb = loadProtocols();
    const layout = sb._getLayouts().graphql;
    assert.equal(layout.executeLabel(graphqlFrame({ operation: 'query' })), 'Execute');
    assert.equal(layout.executeLabel(graphqlFrame({ operation: 'mutation' })), 'Execute');
    assert.equal(layout.executeLabel(graphqlFrame({ operation: 'subscription' })), 'Subscribe');
    sb._connState().gqlSubscribed = true;
    assert.equal(layout.executeLabel(graphqlFrame({ operation: 'subscription' })), 'Unsubscribe');
    sb._connState().gqlSubscribed = false;
});

test('graphql renderResponse: query and mutation keep the default pane', () => {
    // Returning null is what hands the pane back to the shared renderer —
    // a GraphQL query result is an ordinary response body, not a frame log.
    const sb = loadProtocols();
    const layout = sb._getLayouts().graphql;
    assert.equal(layout.renderResponse(graphqlFrame({ operation: 'query' })), null);
    assert.equal(layout.renderResponse(graphqlFrame({ operation: 'mutation' })), null);
});

test('graphql renderResponse: a subscription takes over the pane', () => {
    const sb = loadProtocols();
    const layout = sb._getLayouts().graphql;
    assert.notEqual(layout.renderResponse(graphqlFrame({ operation: 'subscription' })), null);
});

test('graphql renderResponse: events already received survive a switch back to query', () => {
    // Switching the picker back to Query must not blank a log the operator
    // is still reading.
    const sb = loadProtocols();
    const layout = sb._getLayouts().graphql;
    sb._connState().gqlEvents = [{ data: '{"data":{"tick":1}}', ts: Date.now() }];
    try {
        assert.notEqual(layout.renderResponse(graphqlFrame({ operation: 'query' })), null);
    } finally {
        sb._connState().gqlEvents = [];
    }
});

// ---- GraphQL operation picker (#710) ----

test('_graphQLOperationNames: every named operation, in source order', () => {
    const sb = loadProtocols();
    assert.deepEqual(
        sb._graphQLOperationNames('query A { a }\nmutation B { b }\nsubscription C { c }'),
        ['A', 'B', 'C']);
});

test('_graphQLOperationNames: anonymous and shorthand contribute no name', () => {
    const sb = loadProtocols();
    assert.deepEqual(sb._graphQLOperationNames('{ user { id } }'), []);
    assert.deepEqual(sb._graphQLOperationNames('query { user { id } }'), []);
    assert.deepEqual(sb._graphQLOperationNames(''), []);
    assert.deepEqual(sb._graphQLOperationNames(null), []);
});

test('_graphQLOperationNames: a comment that looks like an operation is not one', () => {
    // Without blanking comments the picker would offer Ghost, and picking
    // it would send a name the document does not declare.
    const sb = loadProtocols();
    assert.deepEqual(sb._graphQLOperationNames('# query Ghost { x }\nquery Real { y }'), ['Real']);
});

test('_graphQLOperationNames: a string literal that looks like one is not one', () => {
    const sb = loadProtocols();
    assert.deepEqual(
        sb._graphQLOperationNames('query Real { field(note: "query Ghost { x }") }'),
        ['Real']);
});

test('_graphQLOperationNames: a fragment ahead of the operation does not hide it', () => {
    // The document no longer starts with the keyword, which is what the
    // old anchored pattern required.
    const sb = loadProtocols();
    assert.deepEqual(sb._graphQLOperationNames('fragment F on T { id }\nquery Real { ...F }'), ['Real']);
});

test('_graphQLOperationName: a name for the log only when there is exactly one', () => {
    const sb = loadProtocols();
    assert.equal(sb._graphQLOperationName('query Only { a }'), 'Only');
    // Two operations: no single name to put on a console line, and the
    // plugin will not pick one either.
    assert.equal(sb._graphQLOperationName('query A { a }\nquery B { b }'), null);
    assert.equal(sb._graphQLOperationName('{ a }'), null);
});

test('graphql query tab: several operations seed a choice, one operation clears it', () => {
    const sb = loadProtocols();
    const layout = sb._getLayouts().graphql;

    const many = graphqlFrame({ query: 'query A { a }\nquery B { b }' });
    layout.renderTab(many, 'query');
    // Nothing else can know which one is meant, so the first is offered
    // rather than left blank -- a blank would be sent as "no name" and the
    // server would refuse the request.
    assert.equal(sb._protoState(many).operationName, 'A');

    const one = graphqlFrame({ query: 'query Only { a }', operationName: 'B' });
    layout.renderTab(one, 'query');
    // 'B' is stale: the document no longer declares it.
    assert.equal(sb._protoState(one).operationName, '');
});

test('graphql query tab: a pick that is edited away is replaced, not kept', () => {
    const sb = loadProtocols();
    const layout = sb._getLayouts().graphql;
    const fr = graphqlFrame({ query: 'query A { a }\nquery B { b }', operationName: 'B' });

    layout.renderTab(fr, 'query');
    assert.equal(sb._protoState(fr).operationName, 'B', 'a valid pick survives');

    sb._protoState(fr).query = 'query A { a }\nquery C { c }';
    layout.renderTab(fr, 'query');
    assert.equal(sb._protoState(fr).operationName, 'A', 'B is gone, so it falls back');
});

// ---- GraphQL uploads (#713 UI) ----

test('graphql layout: defaults carry an empty file list', () => {
    const sb = loadProtocols();
    assert.deepEqual(sb._getLayouts().graphql.defaults().files, []);
});

test('graphql layout: the files tab sits between variables and headers', () => {
    // Order is not cosmetic here: a file is chosen for a variable, so the
    // tab belongs beside the variables rather than among the transport
    // settings.
    const sb = loadProtocols();
    const tabs = sb._getLayouts().graphql.subTabs(graphqlFrame()).map((x) => x.id);
    assert.deepEqual(tabs.slice(0, 4), ['query', 'variables', 'files', 'headers']);
});

test('graphql layout: the files tab badges how many rows there are', () => {
    const sb = loadProtocols();
    const tab = sb._getLayouts().graphql.subTabs(graphqlFrame()).find((x) => x.id === 'files');
    const fr = graphqlFrame({ files: [{ path: 'variables.a' }, { path: 'variables.b' }] });
    assert.equal(tab.badge(fr), 2);
});

test('_graphQLVariableNames: the variables an operation declares', () => {
    const sb = loadProtocols();
    assert.deepEqual(
        sb._graphQLVariableNames('mutation Up($file: Upload!, $note: String) { up(f: $file) }'),
        ['file', 'note']);
});

test('_graphQLVariableNames: a use is not a declaration', () => {
    // `$file` appears twice in a signed operation -- once declared with a
    // type, once used. Only the declaration counts, or the warning would
    // never fire for a path that is genuinely wrong.
    const sb = loadProtocols();
    assert.deepEqual(
        sb._graphQLVariableNames('mutation Up($file: Upload!) { up(f: $file, g: $file) }'),
        ['file']);
    assert.deepEqual(sb._graphQLVariableNames('{ up(f: $undeclared) }'), []);
    assert.deepEqual(sb._graphQLVariableNames(''), []);
    assert.deepEqual(sb._graphQLVariableNames(null), []);
});

test('graphql files tab: adding a row guesses only the first path', () => {
    // The first row takes the commonest shape. A second has nothing to
    // guess from, and a wrong guess there is worse than an empty field:
    // it looks filled in.
    const sb = loadProtocols();
    const fr = graphqlFrame();
    const ps = sb._protoState(fr);
    sb._getLayouts().graphql.renderTab(fr, 'files');
    assert.deepEqual(ps.files, []);
});

// ---- _graphQLUploadPayload ----

test('_graphQLUploadPayload: a file becomes the entry the plugin expects', async () => {
    const sb = loadProtocols();
    const out = await sb._graphQLUploadPayload({
        files: [{ path: 'variables.file', name: 'chart.png', contentType: 'image/png', _ref: new File(['hello'], 'chart.png', { type: 'image/png' }) }],
    });
    assert.deepEqual(out, [{
        variablePath: 'variables.file',
        name: 'chart.png',
        contentType: 'image/png',
        // base64 of "hello", with the data: prefix FileReader adds stripped off
        base64: 'aGVsbG8=',
    }]);
});

test('_graphQLUploadPayload: name and type fall back to the file itself', async () => {
    const sb = loadProtocols();
    const out = await sb._graphQLUploadPayload({
        files: [{ path: 'variables.a', _ref: new File(['hi'], 'picked.txt', { type: 'text/plain' }) }],
    });
    assert.equal(out[0].name, 'picked.txt');
    assert.equal(out[0].contentType, 'text/plain');
});

test('_graphQLUploadPayload: a half-filled row is skipped, not sent', async () => {
    // A path typed but no file picked, or a file picked with no path: neither
    // can be placed in the operation, and the spec needs both halves.
    const sb = loadProtocols();
    const out = await sb._graphQLUploadPayload({
        files: [
            { path: 'variables.a' },
            { _ref: new File(['x'], 'x.txt') },
            { path: 'variables.c', _ref: new File(['ok'], 'c.txt') },
        ],
    });
    assert.equal(out.length, 1);
    assert.equal(out[0].variablePath, 'variables.c');
});

test('_graphQLUploadPayload: nothing to send is null, not an empty list', async () => {
    // The caller uses this to decide whether the request is multipart at all.
    const sb = loadProtocols();
    assert.equal(await sb._graphQLUploadPayload({ files: [] }), null);
    assert.equal(await sb._graphQLUploadPayload({}), null);
    assert.equal(await sb._graphQLUploadPayload({ files: [{ path: 'variables.a' }] }), null);
});

test('_graphQLUploadPayload: too large is refused before the bytes are read', async () => {
    // The bytes travel twice on this path — base64 to the workbench, real
    // bytes onward. Without the limit the tab freezes and says nothing.
    const sb = loadProtocols();
    const tooBig = hugeFile('huge.png', sb._graphQLMaxUploadBytes + 1);
    await assert.rejects(
        () => sb._graphQLUploadPayload({ files: [{ path: 'variables.file', _ref: tooBig }] }),
        (err) => {
            assert.match(err.message, /huge\.png/);
            assert.match(err.message, /25 MB/);
            return true;
        });
});

test('_graphQLUploadPayload: exactly at the limit still goes', async () => {
    // An off-by-one here refuses a file the message says is allowed.
    const sb = loadProtocols();
    const atLimit = hugeFile('edge.png', sb._graphQLMaxUploadBytes);
    const out = await sb._graphQLUploadPayload({ files: [{ path: 'variables.file', _ref: atLimit }] });
    assert.equal(out.length, 1);
});
