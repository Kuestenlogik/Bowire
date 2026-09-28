// #250 Phase 2 — where a dragged tab lands.
//
// The zone is also the hint on screen, so the rule is: only return a zone
// that the drop will honour. Before Phase 2 it did not — with two panes the
// edge band still showed, while the drop only moved the tab. What is pinned
// here is that promise, per layout: one pane offers all four edges (that is
// how a row or a stack begins), several offer only the edges along the row,
// and a full row offers none.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const prologue = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/prologue.js'), 'utf8');

/** paneDropZone, snipped out of the fragment — it is a bundle piece and cannot be imported. */
function load() {
    const sig = 'function paneDropZone(rect, clientX, clientY, paneCount, orientation) {';
    const start = prologue.indexOf(sig);
    assert.ok(start >= 0, 'paneDropZone not found in prologue.js');
    let depth = 0, i = prologue.indexOf('{', start);
    for (; i < prologue.length; i++) {
        if (prologue[i] === '{') depth++;
        else if (prologue[i] === '}') { depth--; if (depth === 0) { i++; break; } }
    }
    const max = /var PANE_MAX = (\d+);/.exec(prologue);
    assert.ok(max, 'PANE_MAX not found');
    return new Function(`var PANE_MAX = ${max[1]};\n${prologue.slice(start, i)}\nreturn paneDropZone;`)();
}

const zone = load();
const rect = { left: 0, top: 0, width: 1000, height: 800 };
// A point by fraction of the pane: at(0.1, 0.5) is the middle of the left quarter.
const at = (fx, fy) => [rect.left + rect.width * fx, rect.top + rect.height * fy];

test('one pane: all four edges open a pane, because that choice sets the orientation', () => {
    assert.equal(zone(rect, ...at(0.1, 0.5), 1, 'row'), 'left');
    assert.equal(zone(rect, ...at(0.9, 0.5), 1, 'row'), 'right');
    assert.equal(zone(rect, ...at(0.5, 0.1), 1, 'row'), 'top');
    assert.equal(zone(rect, ...at(0.5, 0.9), 1, 'row'), 'bottom');
    assert.equal(zone(rect, ...at(0.5, 0.5), 1, 'row'), 'center');
});

test('side by side: only left and right split — top and bottom would mean turning every pane', () => {
    assert.equal(zone(rect, ...at(0.1, 0.5), 2, 'row'), 'left');
    assert.equal(zone(rect, ...at(0.9, 0.5), 2, 'row'), 'right');
    assert.equal(zone(rect, ...at(0.5, 0.1), 2, 'row'), 'center');
    assert.equal(zone(rect, ...at(0.5, 0.9), 2, 'row'), 'center');
});

test('stacked: only top and bottom split', () => {
    assert.equal(zone(rect, ...at(0.5, 0.1), 2, 'column'), 'top');
    assert.equal(zone(rect, ...at(0.5, 0.9), 2, 'column'), 'bottom');
    assert.equal(zone(rect, ...at(0.1, 0.5), 2, 'column'), 'center');
    assert.equal(zone(rect, ...at(0.9, 0.5), 2, 'column'), 'center');
});

test('a full row offers no edge at all — the band would promise a pane that cannot open', () => {
    // The bug this replaces: with the maximum reached the hint still showed a
    // split band, and the drop quietly did a move instead.
    for (const orientation of ['row', 'column']) {
        for (const [fx, fy] of [[0.1, 0.5], [0.9, 0.5], [0.5, 0.1], [0.5, 0.9], [0.5, 0.5]]) {
            assert.equal(zone(rect, ...at(fx, fy), 3, orientation), 'center',
                `${orientation} at (${fx}, ${fy})`);
        }
    }
});

test('in a corner the nearer edge wins', () => {
    // 5 % from the left, 20 % from the top: left is nearer.
    assert.equal(zone(rect, ...at(0.05, 0.2), 1, 'row'), 'left');
    // 20 % from the left, 5 % from the top: top is nearer.
    assert.equal(zone(rect, ...at(0.2, 0.05), 1, 'row'), 'top');
});

test('a pane with no size on screen is never all edge', () => {
    // A hidden or collapsed pane reports a zero box. Dividing by it would
    // make every point an edge; the middle is the safe reading.
    assert.equal(zone({ left: 0, top: 0, width: 0, height: 0 }, 0, 0, 1, 'row'), 'center');
    assert.equal(zone(null, 10, 10, 1, 'row'), 'center');
});
