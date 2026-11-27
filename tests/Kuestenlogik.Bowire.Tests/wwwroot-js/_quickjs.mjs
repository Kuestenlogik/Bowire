// #1062 — the vendored QuickJS, for the suites that run scripts the way the
// workbench does: in the interpreter, not in Node's own engine.
//
// quickjs.js is the classic script the workbench loads; it is run in a
// context of its own here, as the page runs it, and hands back the global it
// publishes. sandboxFetch answers the two other requests ensureScriptSandbox
// makes (the wasm and scripts.js as text) from the files in the repository,
// and passes anything else to the suite's own handler.

import vm from 'node:vm';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const VENDOR = resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/vendor/quickjs');

export const QUICKJS_SRC = readFileSync(join(VENDOR, 'quickjs.js'), 'utf8');
const WASM = readFileSync(join(VENDOR, 'quickjs.wasm'));
export const SCRIPTS_JS = readFileSync(resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/scripts.js'), 'utf8');

export function loadBowireQuickJS() {
    const context = vm.createContext({
        console, WebAssembly, TextDecoder, TextEncoder, URL, setTimeout, clearTimeout, performance,
    });
    vm.runInContext(QUICKJS_SRC + '\nglobalThis.BowireQuickJS = BowireQuickJS;', context, { filename: 'quickjs.js' });
    return context.BowireQuickJS;
}

export function sandboxFetch(other) {
    return function (url, init) {
        if (url.includes('/api/ui/script-sandbox/quickjs.wasm')) {
            const bytes = WASM.buffer.slice(WASM.byteOffset, WASM.byteOffset + WASM.byteLength);
            return Promise.resolve({ ok: true, status: 200, arrayBuffer: () => Promise.resolve(bytes) });
        }
        if (url.includes('/api/ui/script-sandbox/scripts.js')) {
            return Promise.resolve({ ok: true, status: 200, text: () => Promise.resolve(SCRIPTS_JS) });
        }
        if (other) return other(url, init);
        return Promise.resolve({ ok: false, status: 404 });
    };
}
