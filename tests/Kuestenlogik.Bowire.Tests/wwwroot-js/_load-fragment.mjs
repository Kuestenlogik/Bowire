// Shared loader for the wwwroot/js fragment unit tests (#367).
//
// The frontend is vanilla JS: every fragment under
// src/Kuestenlogik.Bowire/wwwroot/js/*.js is concatenated into
// prologue.js's IIFE in production, so it can't be `import`ed — it reads
// a bag of host-provided free names (localStorage, wsKey, render, el,
// services, window, …) straight out of that enclosing scope.
//
// The tests used to recreate that scope with
// `new Function(prelude + SRC + postlude)`. That works functionally, but
// V8 attributes the coverage of a `new Function(...)` body to an
// anonymous `evalmachine.<anonymous>` script, which node's coverage
// reporter drops — so `node --test --experimental-test-coverage`
// reported a vacuous "all files 100 %" with zero fragments tracked.
//
// `vm.compileFunction` fixes that: it compiles the fragment as the body
// of a function whose parameters are the host names, and — crucially —
// lets us set `filename` to the fragment's real source path. V8 then
// attributes coverage to that file and emits a proper `SF:` lcov record.
//
// Two rules keep the reported line numbers aligned with the source:
//   * nothing is prepended before the fragment (host names arrive as
//     parameters, not a prelude), so the fragment still starts at line 1;
//   * the `postlude` that returns the fragment's exports is appended
//     AFTER the fragment, so it never shifts the fragment's own lines.

import vm from 'node:vm';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));

// #117 — `t` is one of those host-provided free names, and since the sweep
// began it is one every fragment may reach for. Rather than make each suite
// stub it, the loader supplies one that reads the real English catalogue.
//
// Resolving against en.json rather than returning the key keeps existing
// assertions on English UI text meaningful — a test that says the cell reads
// "OK" still checks what the operator sees — and it means a key that never
// made it into the catalogue shows up in a test as its own raw name, which is
// exactly how it would show up on screen.
const CATALOGUE = (() => {
    const path = resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/locales/en.json');
    const text = readFileSync(path, 'utf8')
        .replace(/^\s*\/\/.*$/gm, '')          // the notes-to-translators lines
        .replace(/,(\s*[}\]])/g, '$1');        // and the trailing comma they allow
    return JSON.parse(text);
})();

/**
 * The English translator, mirroring i18n.js: `{name}` is substituted,
 * `{{name}}` is Bowire's own variable syntax and survives untouched.
 *
 * Exported because a few suites build their own `new Function(...)` harness
 * instead of calling compileFragment, and they need the same `t` — one
 * implementation, so the two cannot drift.
 */
export function t(key, params) {
    const text = CATALOGUE[key];
    if (typeof text !== 'string' || text === '') return key;
    if (!params) return text;
    return text.replace(/(?<!\{)\{([a-zA-Z0-9_]+)\}(?!\})/g, (whole, name) =>
        Object.prototype.hasOwnProperty.call(params, name) ? String(params[name]) : whole);
}

/** tNodes, for the same reason: a fragment that reaches for one reaches for both. */
export function tNodes(key, slot, nodes, params) {
    const text = t(key, params);
    const marker = `{${slot}}`;
    const at = text.indexOf(marker);
    if (at < 0) return [text];
    const middle = Array.isArray(nodes) ? nodes : [nodes];
    return [text.slice(0, at)].concat(middle, [text.slice(at + marker.length)]);
}

// The same two functions as source, for injection into a compiled fragment.
// Built from the live functions rather than written out twice, so the two
// copies cannot drift.
//
// Two constraints shape this block. It is appended AFTER the fragment, so only
// `function` declarations reach the top of the compiled body — a `const t`
// would sit in the temporal dead zone for anything translating at module
// level. And the catalogue is substituted *into* the function rather than
// referenced from a `var` beside it: a `var` is hoisted but its assignment is
// not, so a fragment that returns early left `t` looking up keys on
// `undefined`. map.js does exactly that, and the failure read as
// "Cannot read properties of undefined (reading 'map.label')" — a translation
// bug on its face, a hoisting one underneath.
const DEFAULT_T = `
${t.toString().replace('CATALOGUE', JSON.stringify(CATALOGUE))}
${tNodes.toString()}
`;

/**
 * Compile a wwwroot/js fragment for unit testing under real V8 coverage.
 *
 * @param {string}   srcRelPath  Fragment path, relative to this file
 *                               (e.g. '../../../src/.../coverage.js').
 * @param {string[]} hostNames   Names injected as function parameters —
 *                               typically the per-call inputs (e.g.
 *                               `state`). The bulk of the host stubs the
 *                               fragment reads (localStorage, wsKey,
 *                               render, …) are instead declared in
 *                               `appended` as hoisted `var` / `function`
 *                               declarations, so they never shift the
 *                               fragment's line numbers.
 * @param {string}   appended    Code appended after the fragment: the old
 *                               prelude (now hoisted host stubs) plus the
 *                               postlude that `return`s the fragment's
 *                               exports. Because it lands AFTER the
 *                               fragment, the fragment's own lines stay
 *                               aligned with the source file, while
 *                               `var`/`function` hoisting still makes the
 *                               stubs visible to it.
 * @returns {(hostValues?: Record<string, unknown>) => unknown}
 *          A loader: pass an object keyed by `hostNames`, get the exports.
 */
export function compileFragment(srcRelPath, hostNames, appended) {
    const filename = resolve(__dirname, srcRelPath);
    const src = readFileSync(filename, 'utf8');
    // filename => real coverage attribution; postlude appended => the
    // fragment's own line numbers stay aligned with the source file.
    // Skip the stub when the caller injects its own `t` as a host name, and
    // when the fragment IS the translation layer — i18n.js declares the real
    // t(), and appending a stub after it would override the very function
    // that suite exists to test.
    const declaresOwnT = /\bfunction\s+t\s*\(/.test(src);
    const injectT = !hostNames.includes('t') && !declaresOwnT;
    // The stub goes in before `appended` so a suite that declares its own `t`
    // there still wins — the later function declaration survives hoisting.
    // Both land after the fragment, so its line numbers stay aligned with the
    // source and coverage attribution holds.
    const fn = vm.compileFunction(
        src + '\n' + (injectT ? DEFAULT_T : '') + (appended || ''),
        hostNames,
        { filename },
    );
    const loader = (hostValues) =>
        fn(...hostNames.map((n) => (hostValues || {})[n]));
    // Some suites also assert against the raw fragment text (e.g. "module
    // state is declared with `var`, not `let`/`const`"); expose it so they
    // don't re-read the file.
    loader.source = src;
    loader.filename = filename;
    return loader;
}

/**
 * Read a fragment's raw source text (relative to this file). For the few
 * suites that inspect the source directly without compiling it.
 *
 * @param {string} srcRelPath
 * @returns {string}
 */
export function readFragment(srcRelPath) {
    return readFileSync(resolve(__dirname, srcRelPath), 'utf8');
}
