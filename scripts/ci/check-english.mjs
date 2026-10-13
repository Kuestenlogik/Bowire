#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #777 - everything in this repository is English: code, comments,
// configuration, docs, tests, the roadmap. Another language belongs only in
// a translation resource the product ships (the UI catalogues under
// wwwroot/locales/) or in data the code has to read.
//
// This guard reads every tracked text file and reports each line that reads
// as German, as file:line, and exits non-zero while there is any. It is a
// heuristic: a line counts as German when it carries at least three distinct
// German function words and more of them than English ones, when a word
// with an umlaut or ß stands beside a German function word, or when a
// quotation opening on the line has two German words and no English ones.
// That keeps names such as Küstenlogik out of it and catches a German
// sentence quoted inside an English comment.
//
// Where German is right, say so:
//   - a whole file: an entry in scripts/ci/english-allowlist.json, with the
//     reason;
//   - one line: `lang-exempt: <reason>` on that line or on the line above it.
//
//   node scripts/ci/check-english.mjs            # the whole repository
//   node scripts/ci/check-english.mjs <file>...  # just these files

import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');

// Words that are German and are not English words or common identifiers.
// Short words that English or code also use (an, in, am, im, so, man, die,
// was, also, war, es, um, er) are left out on purpose.
const GERMAN = new Set(`
und oder nicht kein keine keinen keinem keiner nichts ist sind wird werden wurde wurden
worden sein seine seinen ihre ihren hat haben hatte hätte habe kann können konnte könnte
muss müssen musste müsste soll sollen sollte sollten darf dürfen würde würden wäre waere
ich wir sie ihr mich mir dich dir uns euch sich
der das dem den des ein eine einen einem einer eines dieser diese dieses diesen diesem
jeder jede jedes alle alles etwas mehr weniger viel viele sehr ganz gar
auch noch nur schon jetzt immer wieder dann denn weil wenn dass daß damit obwohl sondern
aber doch eigentlich vielleicht einfach gleich bitte warum wieso weshalb wie wo wohin woher
hier dort oben unten links rechts zwischen neben hinter vor nach seit bis ohne gegen durch
für fuer über ueber unter mit bei beim zum zur vom aus auf ins
gibt geht gehen ging macht machen zeigt zeigen sieht sehen steht stehen liegt liegen
fehlt fehlen funktioniert klappt passiert bleibt bleiben kommt kommen gemacht geprüft geprueft
leer leere leeren neu neue neuen neues alte alten gut besser richtig falsch
datei dateien fehler seite fenster leiste einstellungen knopf schaltfläche
`.trim().split(/\s+/));

const ENGLISH = new Set(`
the a and or not is are were be been this that these those with for of to on
at by from it its as but if then when which who what why how can could should would you
`.trim().split(/\s+/));

const UMLAUT = /[äöüßÄÖÜ]/;
const WORD = /[A-Za-zÄÖÜäöüß]+/g;

/** Why a line reads as German, or null when it does not. */
export function germanReason(line) {
    const words = line.match(WORD) ?? [];
    const german = new Set();
    let englishCount = 0;
    let umlautWord = null;
    for (const raw of words) {
        const w = raw.toLowerCase();
        if (GERMAN.has(w)) german.add(w);
        if (ENGLISH.has(w)) englishCount++;
        if (!umlautWord && UMLAUT.test(raw)) umlautWord = raw;
    }
    if (german.size >= 3 && german.size > englishCount) {
        return `German words: ${[...german].slice(0, 6).join(', ')}`;
    }
    if (umlautWord && german.size >= 1 && !GERMAN.has(umlautWord.toLowerCase())) {
        return `"${umlautWord}" beside German words: ${[...german].slice(0, 6).join(', ')}`;
    }
    if (umlautWord && german.size >= 2) {
        return `German words: ${[...german].slice(0, 6).join(', ')}`;
    }
    // A quotation that opens at the end of an English line: judge the quoted
    // part on its own, so the English around it does not outvote it.
    const quote = line.match(/['"„“‚‘]([^'"„“‚‘]*)$/);
    if (quote) {
        const q = (quote[1].match(WORD) ?? []).map(w => w.toLowerCase());
        const qGerman = new Set(q.filter(w => GERMAN.has(w)));
        if (qGerman.size >= 2 && !q.some(w => ENGLISH.has(w))) {
            return `German quotation: ${[...qGerman].slice(0, 6).join(', ')}`;
        }
    }
    return null;
}

const EXEMPT = /lang-exempt:\s*\S/;

/** Every German line of one file's text, minus the ones marked exempt. */
export function findGerman(text) {
    const lines = text.split(/\r?\n/);
    const hits = [];
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        if (line.length > 4000) continue; // minified or generated data
        if (EXEMPT.test(line) || (i > 0 && EXEMPT.test(lines[i - 1]))) continue;
        const reason = germanReason(line);
        if (reason) hits.push({ line: i + 1, text: line.trim(), reason });
    }
    return hits;
}

/** The allowlist: { path, reason } entries; a path ending in / covers a folder. */
export function loadAllowlist(file = path.join(here, 'english-allowlist.json')) {
    const entries = JSON.parse(readFileSync(file, 'utf8'));
    for (const e of entries) {
        if (!e.path || !e.reason || !e.reason.trim()) {
            throw new Error(`english-allowlist.json: every entry needs a path and a reason (${JSON.stringify(e)})`);
        }
    }
    return entries;
}

export function isAllowed(file, allowlist) {
    return allowlist.some(e => e.path.endsWith('/') ? file.startsWith(e.path) : file === e.path);
}

const BINARY = /\.(png|jpe?g|gif|ico|webp|avif|bmp|svg|woff2?|ttf|otf|eot|pdf|zip|gz|tgz|nupkg|snupkg|dll|exe|so|dylib|snk|pfx|p12|mp3|mp4|webm|wasm|bin|db|sqlite)$/i;

/** All findings over the given files (repository-relative paths). */
export function scan(files, { root = repoRoot, allowlist = loadAllowlist() } = {}) {
    const findings = [];
    for (const file of files) {
        if (BINARY.test(file) || isAllowed(file, allowlist)) continue;
        let text;
        try { text = readFileSync(path.join(root, file), 'utf8'); } catch { continue; }
        if (text.includes('\0')) continue;
        for (const hit of findGerman(text)) findings.push({ file, ...hit });
    }
    return findings;
}

export function trackedFiles(root = repoRoot) {
    return execFileSync('git', ['ls-files', '-z'], { cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 })
        .split('\0').filter(Boolean);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const args = process.argv.slice(2);
    const files = args.length ? args.map(a => path.relative(repoRoot, path.resolve(a)).split(path.sep).join('/')) : trackedFiles();
    const findings = scan(files);
    for (const f of findings) console.log(`${f.file}:${f.line}: ${f.reason}\n    ${f.text.slice(0, 160)}`);
    if (findings.length) {
        console.error(`\n${findings.length} line(s) read as German. Translate them, or - when German is right there `
            + `(a translation resource, data the code reads) - add \`lang-exempt: <reason>\` to the line or an entry `
            + `with its reason to scripts/ci/english-allowlist.json.`);
        process.exit(1);
    }
    console.log(`English only: ${files.length} file(s) checked, nothing reads as German.`);
}
