#!/usr/bin/env node
/**
 * Vendors the host side of MCP Apps — the AppBridge from
 * @modelcontextprotocol/ext-apps (MIT) with what it needs from the MCP SDK and
 * zod — into src/Kuestenlogik.Bowire/wwwroot/vendor/mcp-apps/ (#810).
 *
 * The workbench hosts an MCP App in a sandboxed iframe and talks to it over
 * postMessage in the dialect the spec defines (ui/initialize, the tool input
 * and result, tools/call forwarded to the server). AppBridge is the reference
 * implementation of that host side; Bowire uses it rather than a bridge of
 * its own.
 *
 * Output:
 *   mcp-app-bridge.js  one classic script publishing `BowireMcpAppBridge`
 *                      ({ AppBridge, PostMessageTransport, buildAllowAttribute,
 *                      getToolUiResourceUri, RESOURCE_MIME_TYPE }), with zod in
 *                      jitless mode, so nothing is compiled from strings
 *                      under the workbench's Content-Security-Policy.
 *   LICENSE            the licences of what is bundled.
 *
 * Run:
 *   node scripts/vendor/mcp-app-bridge.mjs                  # the pinned version
 *   node scripts/vendor/mcp-app-bridge.mjs --version 2.0.4
 *
 * Needs npm (to fetch the packages and esbuild into a temporary directory;
 * nothing is added to the repository's package.json).
 */
import { execSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync, mkdirSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(__dirname, '..', '..');
const OUT_DIR = join(REPO, 'src', 'Kuestenlogik.Bowire', 'wwwroot', 'vendor', 'mcp-apps');
const PINNED_VERSION = '2.0.4';
const ESBUILD_VERSION = '0.28.2';

const args = process.argv.slice(2);
const version = args.includes('--version') ? args[args.indexOf('--version') + 1] : PINNED_VERSION;
if (!/^\d+\.\d+\.\d+$/.test(version)) throw new Error('--version takes a plain version, e.g. 2.0.4');

function log(m) { console.log(m); }

const work = mkdtempSync(join(tmpdir(), 'bowire-mcp-apps-'));
let esbuild = null;
try {
    log(`Fetching @modelcontextprotocol/ext-apps ${version}, its peers and esbuild ${ESBUILD_VERSION} into ${work} …`);
    writeFileSync(join(work, 'package.json'), JSON.stringify({ name: 'bowire-mcp-apps-vendor', private: true }));
    execSync(`npm install --silent --no-audit --no-fund @modelcontextprotocol/ext-apps@${version} `
        + '@modelcontextprotocol/client@^2 @modelcontextprotocol/core@^2 zod@^4 '
        + `esbuild@${ESBUILD_VERSION}`,
    { cwd: work, stdio: ['ignore', 'inherit', 'inherit'] });

    writeFileSync(join(work, 'entry.mjs'), [
        "import { config } from 'zod/v4';",
        '// No schema compiled from a string: the workbench refuses eval.',
        'config({ jitless: true });',
        "export { AppBridge, PostMessageTransport, buildAllowAttribute, getToolUiResourceUri, RESOURCE_MIME_TYPE } from '@modelcontextprotocol/ext-apps/app-bridge';",
        '',
    ].join('\n'));

    esbuild = (await import('file://' + join(work, 'node_modules', 'esbuild', 'lib', 'main.js').replace(/\\/g, '/'))).default;
    const built = await esbuild.build({
        entryPoints: [join(work, 'entry.mjs')],
        bundle: true,
        format: 'iife',
        globalName: 'BowireMcpAppBridge',
        platform: 'browser',
        target: 'es2020',
        minify: true,
        write: false,
        logLevel: 'error',
        legalComments: 'none',
    });
    const bundle = built.outputFiles[0].text;
    if (/[^.\w]Function\(|[^.\w]eval\(/.test(bundle)) throw new Error('the bundle evaluates strings; the workbench policy would refuse it');

    const pkg = (name) => join(work, 'node_modules', ...name.split('/'));
    const licence = (name) => {
        const file = ['LICENSE', 'LICENSE.md', 'license'].map((f) => join(pkg(name), f)).find(existsSync);
        const v = JSON.parse(readFileSync(join(pkg(name), 'package.json'), 'utf8')).version;
        return `${name} ${v}\n\n` + (file ? readFileSync(file, 'utf8') : '(MIT, see the package)') + '\n';
    };

    const notice = `/*! @modelcontextprotocol/ext-apps ${version} app-bridge with @modelcontextprotocol/client, core and zod (MIT),
 * bundled for Bowire by scripts/vendor/mcp-app-bridge.mjs. Licences: LICENSE next to this file. */\n`;
    mkdirSync(OUT_DIR, { recursive: true });
    writeFileSync(join(OUT_DIR, 'mcp-app-bridge.js'), notice + bundle);
    writeFileSync(join(OUT_DIR, 'LICENSE'), ['@modelcontextprotocol/ext-apps', '@modelcontextprotocol/client',
        '@modelcontextprotocol/core', 'zod'].map(licence).join('\n'));
    log(`Wrote ${OUT_DIR}: mcp-app-bridge.js ${(bundle.length / 1024).toFixed(0)} KB`);
} finally {
    if (esbuild) await esbuild.stop();
    try { rmSync(work, { recursive: true, force: true, maxRetries: 3 }); }
    catch (e) { console.warn(`could not remove ${work}: ${e.message}`); }
}
