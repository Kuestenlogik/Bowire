// #117 — what is still hard-coded, and where.
//
//   node tests/Kuestenlogik.Bowire.Tests/wwwroot-js/untranslated-report.mjs
//   node .../untranslated-report.mjs --write     rewrites the baseline
//   node .../untranslated-report.mjs Recordings  only files matching a pattern
//
// The list is the work queue for the sweep; --write is what a sweep commit runs
// afterwards, so the shrinking number lands in the diff next to the strings it
// removed.

import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { untranslatedCounts, untranslatedSites } from './_untranslated.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const BASELINE = resolve(__dirname, 'untranslated-baseline.json');

const args = process.argv.slice(2);
const write = args.includes('--write');
const filter = args.find((a) => !a.startsWith('--'));

const counts = untranslatedCounts();

if (write) {
    const sorted = Object.fromEntries(Object.entries(counts).sort(([a], [b]) => a.localeCompare(b)));
    writeFileSync(BASELINE, `${JSON.stringify(sorted, null, 2)}\n`, 'utf8');
    const total = Object.values(counts).reduce((a, b) => a + b, 0);
    console.log(`baseline written: ${total} literals in ${Object.keys(counts).length} files`);
} else if (filter) {
    for (const site of untranslatedSites().filter((s) => s.file.includes(filter))) {
        console.log(`${site.file}:${site.line}  ${site.text}`);
    }
} else {
    for (const [file, n] of Object.entries(counts).sort(([, a], [, b]) => b - a)) {
        console.log(String(n).padStart(5), file);
    }
    const total = Object.values(counts).reduce((a, b) => a + b, 0);
    console.log(`----- ${total} literals in ${Object.keys(counts).length} files`);
}
