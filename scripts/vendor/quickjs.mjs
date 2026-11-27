#!/usr/bin/env node
/**
 * Vendors QuickJS (quickjs-emscripten, MIT) — the JavaScript interpreter the
 * workbench runs pre-request and post-response scripts in (#1062) — into
 * src/Kuestenlogik.Bowire/wwwroot/vendor/quickjs/.
 *
 * Why an interpreter: the workbench's Content-Security-Policy refuses
 * 'unsafe-eval', and a script is a string. QuickJS compiled to WebAssembly
 * runs it in a heap of its own, with no reach into the page, its storage or
 * the network, and needs only 'wasm-unsafe-eval'. The server runs the same
 * runtime (wwwroot/js/scripts.js) in Jint; this is its browser twin.
 *
 * Output:
 *   quickjs.js    quickjs-emscripten-core + the release-sync variant's glue,
 *                 bundled into one classic script publishing `BowireQuickJS`
 *                 ({ load(wasmBytes), shouldInterruptAfterDeadline }).
 *   quickjs.wasm  the release-sync interpreter, as upstream builds it.
 *   LICENSE       the licences of both.
 *
 * The bundle is checked before anything is written: it is loaded into a
 * fresh Node context with the wasm and asked to evaluate a script.
 *
 * Run:
 *   node scripts/vendor/quickjs.mjs                    # the pinned version
 *   node scripts/vendor/quickjs.mjs --version 0.32.0
 *
 * Needs npm (to fetch the packages and esbuild into a temporary directory;
 * nothing is added to the repository's package.json).
 */
import { execSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(__dirname, '..', '..');
const OUT_DIR = join(REPO, 'src', 'Kuestenlogik.Bowire', 'wwwroot', 'vendor', 'quickjs');
const PINNED_VERSION = '0.32.0';
const ESBUILD_VERSION = '0.28.2';

const args = process.argv.slice(2);
const version = args.includes('--version') ? args[args.indexOf('--version') + 1] : PINNED_VERSION;

function log(m) { console.log(m); }

const work = mkdtempSync(join(tmpdir(), 'bowire-quickjs-'));
let esbuild = null;
try {
    log(`Fetching quickjs-emscripten ${version} and esbuild ${ESBUILD_VERSION} into ${work} …`);
    writeFileSync(join(work, 'package.json'), JSON.stringify({ name: 'bowire-quickjs-vendor', private: true }));
    if (!/^\d+\.\d+\.\d+$/.test(version)) throw new Error('--version takes a plain version, e.g. 0.32.0');
    execSync(`npm install --silent --no-audit --no-fund quickjs-emscripten-core@${version} `
        + `@jitl/quickjs-wasmfile-release-sync@${version} esbuild@${ESBUILD_VERSION}`,
    { cwd: work, stdio: ['ignore', 'inherit', 'inherit'] });

    // The entry: hand the wasm bytes in, so nothing has to locate the file
    // through import.meta.url (which a classic script does not have).
    writeFileSync(join(work, 'entry.mjs'), [
        "import { newQuickJSWASMModuleFromVariant, newVariant, shouldInterruptAfterDeadline } from 'quickjs-emscripten-core';",
        "import variant from '@jitl/quickjs-wasmfile-release-sync';",
        'export function load(wasmBytes) {',
        '    return newQuickJSWASMModuleFromVariant(newVariant(variant, { wasmBinary: wasmBytes }));',
        '}',
        'export { shouldInterruptAfterDeadline };',
        '',
    ].join('\n'));

    esbuild = (await import('file://' + join(work, 'node_modules', 'esbuild', 'lib', 'main.js').replace(/\\/g, '/'))).default;
    const built = await esbuild.build({
        entryPoints: [join(work, 'entry.mjs')],
        bundle: true,
        format: 'iife',
        globalName: 'BowireQuickJS',
        platform: 'browser',
        target: 'es2020',
        minify: true,
        write: false,
        logLevel: 'error',
        // The glue still resolves the wasm's URL against its own, eagerly,
        // even when the bytes are handed in; it never fetches it. A valid
        // base that names nothing keeps that resolution from throwing.
        define: { 'import.meta.url': '"https://bowire.invalid/quickjs/"' },
    });
    const bundle = built.outputFiles[0].text;
    if (/\bnew Function\b|[^.\w]eval\(/.test(bundle)) throw new Error('the bundle evaluates strings; the workbench policy would refuse it');

    const pkg = (name) => join(work, 'node_modules', ...name.split('/'));
    const wasm = readFileSync(join(pkg('@jitl/quickjs-wasmfile-release-sync'), 'dist', 'emscripten-module.wasm'));
    const licence = (name) => readFileSync(join(pkg(name), 'LICENSE'), 'utf8');

    log('Checking the bundle …');
    const sandbox = { console, WebAssembly, TextDecoder, TextEncoder, URL, setTimeout, clearTimeout, performance };
    vm.createContext(sandbox);
    vm.runInContext(bundle + '\nglobalThis.BowireQuickJS = BowireQuickJS;', sandbox);
    const qjs = await sandbox.BowireQuickJS.load(new Uint8Array(wasm));
    const ctx = qjs.newContext();
    const r = ctx.evalCode('var f = new Function("a", "return a * 6"); JSON.stringify({ v: f(7) })');
    const out = ctx.dump(ctx.unwrapResult(r));
    ctx.dispose();
    if (out !== '{"v":42}') throw new Error('the bundle did not evaluate a script: ' + out);

    const notice = `/*! quickjs-emscripten ${version} (MIT, Jake Teton-Landis; QuickJS MIT, Fabrice Bellard and Charlie Gordon),
 * bundled for Bowire by scripts/vendor/quickjs.mjs. Licences: LICENSE next to this file. */\n`;
    mkdirSync(OUT_DIR, { recursive: true });
    writeFileSync(join(OUT_DIR, 'quickjs.js'), notice + bundle);
    writeFileSync(join(OUT_DIR, 'quickjs.wasm'), wasm);
    writeFileSync(join(OUT_DIR, 'LICENSE'), [
        `quickjs-emscripten-core ${version}`, licence('quickjs-emscripten-core'), '',
        `@jitl/quickjs-wasmfile-release-sync ${version} (QuickJS compiled to WebAssembly)`,
        licence('@jitl/quickjs-wasmfile-release-sync'), '',
    ].join('\n'));
    log(`Wrote ${OUT_DIR}: quickjs.js ${(bundle.length / 1024).toFixed(0)} KB, quickjs.wasm ${(wasm.length / 1024).toFixed(0)} KB`);
} finally {
    // esbuild keeps a service process that holds its binary open.
    if (esbuild) await esbuild.stop();
    try { rmSync(work, { recursive: true, force: true, maxRetries: 3 }); }
    catch (e) { console.warn(`could not remove ${work}: ${e.message}`); }
}
