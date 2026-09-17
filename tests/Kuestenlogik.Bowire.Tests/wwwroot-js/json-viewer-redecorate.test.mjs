// The JSON viewer rebuilds every line element when a node is expanded
// or collapsed, and with them goes everything a decorator had stamped:
// the semantic badges, the map widget's coord paths for the hover-sync.
// The host that decorated the tree leaves the decoration on the tree
// root as `__bowireRedecorate`, and the viewer runs it after each
// rebuild. A single expand used to end the hover-sync until the next
// frame happened to re-render the viewer.
//
// The viewer and the host live in two fragments of one IIFE, so this
// pins the contract at the seam: what the viewer calls, and what the
// host installs.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const read = (rel) => readFileSync(resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/', rel), 'utf8');

/** A function's source by brace matching from its signature. */
function extract(src, signature) {
    const start = src.indexOf(signature);
    assert.ok(start >= 0, `${signature} not found`);
    const open = src.indexOf('{', start);
    let depth = 0, i = open;
    for (; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}') { depth--; if (depth === 0) { i++; break; } }
    }
    return src.slice(start, i);
}

test('the viewer runs the tree root\'s redecorate hook after rebuilding its lines', () => {
    const rebuild = extract(read('helpers.js'), 'function _rebuildViewerLines()');
    // After the lines are appended, not before: the hook stamps the new elements.
    const append = rebuild.indexOf('viewer.appendChild(_jsonViewerLineEl(');
    const hook = rebuild.indexOf('__bowireRedecorate');
    assert.ok(append > 0 && hook > append, 'redecorate runs after the new lines exist');
    // Found on an ancestor: the decorated tree root is the viewer's container, not the viewer.
    assert.match(rebuild, /host = host\.parentNode/);
});

test('the host installs the same decoration it ran as the redecorate hook, on both response paths', () => {
    const main = read('render-main.js');
    const decorate = extract(main, 'function bowireDecorateResponseTree(treeRoot, serviceName, methodName, explicitRoot, where)');
    assert.match(decorate, /treeRoot\.__bowireRedecorate = decorate;/);
    assert.match(decorate, /bowireDecorateResponseTreeForSemantics\(treeRoot, serviceName, methodName\)/);
    assert.match(decorate, /bowireDecorateResponseTreeViaExtensions\(treeRoot, serviceName, methodName, explicitRoot\)/);
    // The stream detail and the unary response both go through it —
    // neither calls the two decorators on its own any more.
    assert.match(main, /bowireDecorateResponseTree\(body, svc\.name, method\.name, parsedFrame, 'stream-detail'\)/);
    assert.match(main, /bowireDecorateResponseTree\(output, svc\.name, method\.name, undefined, 'unary'\)/);
    const direct = main.match(/bowireDecorateResponseTreeViaExtensions\(\s*(body|output),/g) || [];
    assert.deepEqual(direct, [], 'no call site bypasses the hook');
});
