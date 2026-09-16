#!/usr/bin/env node
/**
 * Vendors mil-sym-ts — the MIL-STD-2525D/E multipoint renderer the map
 * widget draws tactical graphics with — into
 * src/Kuestenlogik.Bowire.Map/wwwroot/mil-sym-ts/.
 *
 * The upstream web build is one 7.4 MB ES module: the renderer, its
 * lookup tables, and four tables of single-point icon SVGs (svgd, svge,
 * svg6d, svg6e) that make up 6.3 MB of it. The map never asks mil-sym-ts
 * for a single-point symbol — milsymbol draws those — but a handful of
 * multipoint graphics carry icons of their own: a decision line's
 * decision points, the mines in a mined area, the NBC signs of a
 * contaminated area. Those icons are all control-measure entries (ids
 * starting with the symbol set, `25`), so that is what the tables are
 * cut down to. Everything else — every unit, equipment and installation
 * icon — goes.
 *
 * Then the module is turned into a plain script publishing `C5Ren` on
 * the global, the way milsymbol publishes `ms`, and gzipped. The result
 * is a modified copy of an Apache-2.0 work; the notice at the top of the
 * file and in the licence file says what changed.
 *
 * Nothing is trusted: both the untouched build and the trimmed one are
 * loaded into Chromium and asked to draw every control measure the
 * lookup knows, for three identities, every standard version and one to
 * four points, and the two answers have to match byte for byte before
 * anything is written.
 *
 * Run:
 *   node scripts/vendor/mil-sym-ts.mjs               # the pinned version
 *   node scripts/vendor/mil-sym-ts.mjs --version 2.11.0
 *
 * Needs npm (to fetch the tarball) and the repo's Playwright Chromium.
 */
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync, mkdirSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync, gunzipSync } from 'node:zlib';
import { chromium } from '@playwright/test';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(__dirname, '..', '..');
const OUT_DIR = join(REPO, 'src', 'Kuestenlogik.Bowire.Map', 'wwwroot', 'mil-sym-ts');
const PACKAGE = '@armyc2.c5isr.renderer/mil-sym-ts-web';
const PINNED_VERSION = '2.10.4';
const TABLES = ['svgd', 'svge', 'svg6d', 'svg6e'];
// Control-measure icons (symbol set 25): the full `25` + six-digit entity
// code, the five-digit entity-level fallback the lookup tries next, and
// the octagon the icon renderer keeps on hand.
const KEEP = /^25\d{6}$|^25\d{3}$|^octagon$/;

const args = process.argv.slice(2);
const version = args.includes('--version') ? args[args.indexOf('--version') + 1] : PINNED_VERSION;

function log(m) { console.log(m); }
function mb(n) { return (n / 1048576).toFixed(2) + ' MB'; }

// ---------------------------------------------------------------- fetch

function fetchPackage() {
    const dir = mkdtempSync(join(tmpdir(), 'mil-sym-ts-'));
    log(`Fetching ${PACKAGE}@${version} …`);
    execFileSync('npm', ['pack', `${PACKAGE}@${version}`, '--pack-destination', dir, '--silent'],
        { stdio: ['ignore', 'pipe', 'inherit'], shell: process.platform === 'win32' });
    const tarball = join(dir, `armyc2.c5isr.renderer-mil-sym-ts-web-${version}.tgz`);
    const files = untar(gunzipSync(readFileSync(tarball)));
    const need = (name) => {
        if (!files.has(name)) throw new Error(`${name} not in the tarball`);
        return files.get(name).toString('utf8');
    };
    return { dir, module: need('package/C5Ren.mjs'), license: need('package/LICENSE') };
}

/**
 * The two files out of a tar archive, read here rather than by `tar`:
 * the GNU tar on a Windows Git Bash takes `C:\…` for a remote host.
 * Plain ustar with 512-byte headers is all npm writes.
 */
function untar(buf) {
    const files = new Map();
    let at = 0;
    while (at + 512 <= buf.length) {
        const header = buf.subarray(at, at + 512);
        if (header.every((b) => b === 0)) break;
        const name = header.toString('utf8', 0, 100).replace(/\0.*$/, '');
        const size = parseInt(header.toString('utf8', 124, 136).replace(/\0.*$/, '').trim(), 8) || 0;
        const type = String.fromCharCode(header[156]);
        at += 512;
        if (type === '0' || type === '\0') files.set(name, buf.subarray(at, at + size));
        at += Math.ceil(size / 512) * 512;
    }
    return files;
}

// ------------------------------------------------------------ transform

/** The ES module as a classic script: `globalThis.C5Ren = { … }`. */
function toScript(module, notice) {
    const exportRe = /^export \{ ([^}]+) \};\s*$/m;
    const m = exportRe.exec(module);
    if (!m) throw new Error('export line not found — the upstream build changed shape');
    const names = m[1].split(',').map((s) => s.trim()).filter(Boolean);
    const assign = 'globalThis.C5Ren = { ' + names.map((n) => `${n}: ${n}`).join(', ') + ' };';
    let body = module.replace(exportRe, assign).replace(/^\/\/# sourceMappingURL=.*$/m, '');
    if (/^\s*(import|export)\s/m.test(body)) throw new Error('module syntax left after the transform');
    return notice + '(function () {\n' + body + '\n})();\n';
}

/** Cut each icon table down to the entries `KEEP` names. */
function trimTables(script) {
    let out = script;
    const removed = [];
    for (const table of TABLES) {
        const start = out.indexOf(`//#region src/main/ts/armyc2/c5isr/data/${table}.json`);
        if (start < 0) throw new Error(`table ${table} not found`);
        const end = out.indexOf('//#endregion', start);
        const region = out.slice(start, end);
        const decl = new RegExp(`var (${table}_default) = `);
        const dm = decl.exec(region);
        if (!dm) throw new Error(`declaration of ${table}_default not found`);
        const literalStart = dm.index + dm[0].length;
        const literalEnd = region.lastIndexOf('};') + 1;
        const literal = region.slice(literalStart, literalEnd);
        // The literal is JSON with one bare key; evaluating it is the
        // honest parser for what rolldown emitted.
        const data = new Function('return (' + literal + ');')();
        const all = data.svgdata.SVGElements;
        const kept = all.filter((e) => KEEP.test(String(e.id)));
        removed.push(`${table}: ${all.length} → ${kept.length} entries`);
        const replacement = region.slice(0, literalStart)
            + JSON.stringify({ svgdata: { SVGElements: kept } })
            + region.slice(literalEnd);
        out = out.slice(0, start) + replacement + out.slice(end);
    }
    return { script: out, removed };
}

// --------------------------------------------------------------- verify

/**
 * Every control measure the lookup lists, drawn by `script` in Chromium:
 * three identities (unknown, friend, hostile), every standard version,
 * one to four points. Returns { key: geojson } for the renders that did
 * not come back as an error envelope, plus a count of those that did.
 */
async function renderAll(browser, script, label) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push(e.message));
    await page.setContent('<!doctype html><html><body></body></html>');
    await page.addScriptTag({ content: script });
    const result = await page.evaluate(() => {
        const { WebRenderer, MSLookup } = globalThis.C5Ren;
        const lookup = MSLookup.getInstance();
        const pts = {
            1: '11.5,54',
            2: '10.55,54.36 10.58,54.28',
            3: '10.16,54.11 10.22,54.115 10.25,54.09',
            4: '10.16,54.11 10.22,54.115 10.25,54.09 10.16,54.07',
        };
        const mods = new Map([
            ['T_UNIQUE_DESIGNATION_1', 'X'], ['AM_DISTANCE', '1000,5000'], ['AN_AZIMUTH', '30,90'], ['B_ECHELON', '16'],
        ]);
        const out = {};
        let refused = 0;
        for (const version of [10, 11, 12, 13, 14, 15, 16]) {
            const ids = (lookup.getIDList(version) || []).filter((id) => String(id).startsWith('25'));
            for (const id of ids) {
                for (const identity of ['1', '3', '6']) {
                    const sidc = String(version) + '0' + identity + '25' + '00' + '00' + String(id).slice(2) + '0000';
                    for (const n of [1, 2, 3, 4]) {
                        let r;
                        try {
                            r = WebRenderer.RenderSymbol2D('id', 'n', '', sidc, pts[n], 1200, 800,
                                '10.0,53.8,12.0,54.5', mods, new Map(), WebRenderer.OUTPUT_FORMAT_GEOJSON);
                        } catch (e) { r = 'THROW ' + e.message; }
                        const s = String(r);
                        if (s.startsWith('{"type":"error"')) { refused++; continue; }
                        out[sidc + '/' + n] = s;
                    }
                }
            }
        }
        return { out, refused };
    });
    await page.close();
    if (errors.length) throw new Error(`${label}: page errors: ${errors.join('; ')}`);
    const drawn = Object.keys(result.out).length;
    log(`  ${label}: ${drawn} graphics drawn, ${result.refused} refused by the draw rules`);
    if (drawn < 500) throw new Error(`${label}: only ${drawn} graphics drawn — the build is not working`);
    return result.out;
}

async function verify(fullScript, trimmedScript) {
    log('Verifying in Chromium …');
    const browser = await chromium.launch({ headless: true });
    try {
        const full = await renderAll(browser, fullScript, 'untouched build');
        const trimmed = await renderAll(browser, trimmedScript, 'trimmed build');
        const keys = Object.keys(full);
        const diffs = keys.filter((k) => full[k] !== trimmed[k]);
        if (Object.keys(trimmed).length !== keys.length || diffs.length > 0) {
            throw new Error(`trimmed build differs on ${diffs.length} render(s), e.g. ${diffs.slice(0, 5).join(', ')}`);
        }
        log(`  identical on all ${keys.length} renders`);
    } finally {
        await browser.close();
    }
}

// ----------------------------------------------------------------- main

const { dir, module, license } = fetchPackage();
try {
    const notice = `/*!
 * mil-sym-ts ${version} — https://github.com/missioncommand/mil-sym-ts
 * Copyright: C5ISR ESI. Licensed under the Apache License, Version 2.0
 * (see mil-sym-ts.LICENSE next to this file).
 *
 * MODIFIED by Küstenlogik for Bowire (scripts/vendor/mil-sym-ts.mjs):
 *   - the ES module export is replaced by \`globalThis.C5Ren = {...}\`
 *     so the file loads as a plain <script>;
 *   - the single-point icon tables (svgd, svge, svg6d, svg6e) are cut
 *     down to the control-measure entries (symbol set 25) the multipoint
 *     renderer draws; unit, equipment and installation icons are removed.
 * Multipoint output is verified identical to the unmodified build.
 */
`;
    const full = toScript(module, notice);
    const { script: trimmed, removed } = trimTables(full);
    log('Tables trimmed:');
    for (const r of removed) log('  ' + r);

    await verify(full, trimmed);

    mkdirSync(OUT_DIR, { recursive: true });
    const gz = gzipSync(Buffer.from(trimmed, 'utf8'), { level: 9 });
    writeFileSync(join(OUT_DIR, 'mil-sym-ts.js.gz'), gz);
    writeFileSync(join(OUT_DIR, 'mil-sym-ts.LICENSE'),
        license.trimEnd() + `

--------------------------------------------------------------------------
Modifications by Küstenlogik (Bowire), per section 4(b) of the licence:
mil-sym-ts.js.gz is built from ${PACKAGE}@${version} by
scripts/vendor/mil-sym-ts.mjs. The ES module export is replaced by a
global (\`C5Ren\`), and the single-point icon tables are reduced to the
control-measure entries the multipoint renderer uses. See the notice at
the top of the file.
`);
    log(`Written ${join(OUT_DIR, 'mil-sym-ts.js.gz')}`);
    log(`  upstream module ${mb(Buffer.byteLength(module))}, trimmed script ${mb(Buffer.byteLength(trimmed))}, gzipped ${mb(statSync(join(OUT_DIR, 'mil-sym-ts.js.gz')).size)}`);
} finally {
    rmSync(dir, { recursive: true, force: true });
}
