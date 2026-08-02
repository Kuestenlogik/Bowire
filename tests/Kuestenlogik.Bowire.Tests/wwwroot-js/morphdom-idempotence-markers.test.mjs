// #550 — the marker morphdom keeps, and the one it destroys.
//
// Four helpers guarded their imperative wiring with a `data-*` attribute
// set AFTER a render. morphdom preserves the node but syncs its
// attributes against the freshly built tree, and the fresh tree never
// carries a marker that was written imperatively — so morphAttrs removed
// it on every render and the guard never held. Each render then left
// another ResizeObserver, MutationObserver or listener registered, none
// disconnected, each doing work on every subsequent render.
//
// These tests pin the mechanism against the real vendored morphdom so a
// future author cannot reintroduce the pattern believing it is safe.
// They are a property of morphdom, not of any one call site.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const MORPHDOM_SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/_morphdom.js'),
    'utf8'
);

// A DOM small enough to hand-roll and faithful on the two points that
// matter here: attributes are a synced collection, expando properties
// are not, and node identity is observable.
function makeDom() {
    let idSeq = 0;

    class Attr {
        constructor(name, value) { this.name = name; this.value = value; }
    }

    class Node {
        constructor(tagName) {
            this.nodeType = 1;
            this.nodeName = tagName.toUpperCase();
            this.tagName = this.nodeName;
            this._attrs = new Map();
            this.childNodes = [];
            this.parentNode = null;
            this._id = ++idSeq;
        }
        get attributes() {
            return [...this._attrs.entries()].map(([name, value]) => new Attr(name, value));
        }
        getAttribute(n) { return this._attrs.has(n) ? this._attrs.get(n) : null; }
        setAttribute(n, v) { this._attrs.set(n, String(v)); }
        removeAttribute(n) { this._attrs.delete(n); }
        hasAttribute(n) { return this._attrs.has(n); }
        getAttributeNS(_ns, n) { return this.getAttribute(n); }
        setAttributeNS(_ns, n, v) { this.setAttribute(n, v); }
        removeAttributeNS(_ns, n) { this.removeAttribute(n); }
        appendChild(c) { c.parentNode = this; this.childNodes.push(c); return c; }
        removeChild(c) { this.childNodes = this.childNodes.filter(x => x !== c); c.parentNode = null; return c; }
        insertBefore(c, ref) {
            const i = this.childNodes.indexOf(ref);
            c.parentNode = this;
            if (i < 0) this.childNodes.push(c); else this.childNodes.splice(i, 0, c);
            return c;
        }
        get firstChild() { return this.childNodes[0] || null; }
        get nextSibling() {
            if (!this.parentNode) return null;
            const i = this.parentNode.childNodes.indexOf(this);
            return this.parentNode.childNodes[i + 1] || null;
        }
        get id() { return this.getAttribute('id') || ''; }
        set id(v) { this.setAttribute('id', v); }
        // Deliberately structural: two nodes are equal when their tag,
        // attributes and children agree. This is what makes the fast
        // path in render() miss an imperatively-marked node.
        isEqualNode(other) {
            if (!other || other.nodeName !== this.nodeName) return false;
            if (other._attrs.size !== this._attrs.size) return false;
            for (const [k, v] of this._attrs) {
                if (other._attrs.get(k) !== v) return false;
            }
            if (other.childNodes.length !== this.childNodes.length) return false;
            return this.childNodes.every((c, i) => c.isEqualNode(other.childNodes[i]));
        }
    }

    const document = {
        createElement: (t) => new Node(t),
        createElementNS: (_ns, t) => new Node(t),
    };
    return { document, Node };
}

function loadMorphdom() {
    const { document, Node } = makeDom();
    const sandbox = {
        document,
        Node,
        Element: Node,
        DocumentFragment: class {},
        HTMLElement: Node,
        window: undefined,
    };
    sandbox.window = sandbox;
    const fn = new Function(
        'window', 'document', 'Node', 'Element', 'DocumentFragment', 'HTMLElement',
        MORPHDOM_SRC + '\n; return (typeof morphdom !== "undefined") ? morphdom : window.morphdom;');
    const morphdom = fn(sandbox, document, Node, Node, sandbox.DocumentFragment, Node);
    assert.ok(typeof morphdom === 'function', 'morphdom did not load');
    return { morphdom, document };
}

test('morphdom preserves the node but STRIPS an imperatively-set data-* marker', () => {
    const { morphdom, document } = loadMorphdom();

    // The live tree, as it looks after a render plus imperative wiring.
    const live = document.createElement('div');
    live.setAttribute('class', 'strip');
    live.setAttribute('data-overflow-wired', '1');   // written after render

    // The next render builds the strip WITHOUT the marker — it cannot
    // know about it, because nothing declarative sets it.
    const fresh = document.createElement('div');
    fresh.setAttribute('class', 'strip');

    const before = live;
    morphdom(live, fresh);

    assert.equal(before, live, 'morphdom kept the node');
    assert.equal(live.getAttribute('data-overflow-wired'), null,
        'the guard attribute is gone — this is why the wiring stacked');
});

test('morphdom leaves an expando PROPERTY marker alone', () => {
    const { morphdom, document } = loadMorphdom();

    const live = document.createElement('div');
    live.setAttribute('class', 'strip');
    live._bowireOverflowWired = true;                // the fix

    const fresh = document.createElement('div');
    fresh.setAttribute('class', 'strip');

    morphdom(live, fresh);

    assert.equal(live._bowireOverflowWired, true,
        'the property survives, so the guard holds and nothing re-wires');
});

test('an imperatively added class is stripped too', () => {
    // Which is why bowireWireTabOverflow re-asserts its class on every
    // call instead of only on the first.
    const { morphdom, document } = loadMorphdom();

    const live = document.createElement('div');
    live.setAttribute('class', 'strip bowire-has-overflow-wired');
    const fresh = document.createElement('div');
    fresh.setAttribute('class', 'strip');

    morphdom(live, fresh);

    assert.equal(live.getAttribute('class'), 'strip',
        'the imperative class does not survive a render either');
});

test('N renders leave N observers when the guard is an attribute, 1 when it is a property', () => {
    // The accumulation itself, simulated with the two guard shapes side
    // by side. This is the number that turns a slow page into a dead one:
    // each leaked observer runs its callback on every later render.
    const { morphdom, document } = loadMorphdom();

    function run(useProperty) {
        const strip = document.createElement('div');
        strip.setAttribute('class', 'strip');
        let wired = 0;

        const wire = () => {
            if (useProperty) {
                if (strip._wired) return;
                strip._wired = true;
            } else {
                if (strip.getAttribute('data-wired') === '1') return;
                strip.setAttribute('data-wired', '1');
            }
            wired++;   // stands in for `new ResizeObserver(...).observe(...)`
        };

        wire();
        for (let i = 0; i < 100; i++) {
            const fresh = document.createElement('div');
            fresh.setAttribute('class', 'strip');
            morphdom(strip, fresh);
            wire();
        }
        return wired;
    }

    assert.equal(run(false), 101, 'attribute guard: one wire per render');
    assert.equal(run(true), 1, 'property guard: wired once, ever');
});
