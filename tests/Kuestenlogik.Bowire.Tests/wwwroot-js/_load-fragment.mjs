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
    const fn = vm.compileFunction(src + '\n' + (appended || ''), hostNames, {
        filename,
    });
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
