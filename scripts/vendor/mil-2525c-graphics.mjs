#!/usr/bin/env node
/**
 * Builds the 2525C → 2525D crosswalk for tactical graphics into
 * src/Kuestenlogik.Bowire.Map/wwwroot/mil-sym-ts/2525c-graphics.json.
 *
 * mil-sym-ts draws MIL-STD-2525D and later; it has no tables for the
 * fifteen-letter 2525C codes, and a producer that still speaks C —
 * the TacticalAPI sample's tracks do — would get its phase lines and
 * areas drawn as bare geometry. The standard's own crosswalk is
 * Esri's joint-military-symbology-xml, whose legacy-support table maps
 * every 2525C symbol to its 2525D entity (Apache-2.0). This script
 * takes the tactical-graphics rows out of it — scheme `G`, symbol set
 * 25 on the D side — and keeps only what the widget needs: the C code's
 * category letter and six-letter function id, and the D entity code.
 * Affiliation, status and echelon are not in the table because they
 * translate letter by letter; the widget does that itself.
 *
 * Pinned to a commit so two runs give the same file; bump the pin to
 * pick up upstream corrections.
 *
 * Run:
 *   node scripts/vendor/mil-2525c-graphics.mjs
 */
import { writeFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(__dirname, '..', '..');
const OUT = join(REPO, 'src', 'Kuestenlogik.Bowire.Map', 'wwwroot', 'mil-sym-ts', '2525c-graphics.json');

const REPO_URL = 'https://github.com/Esri/joint-military-symbology-xml';
const COMMIT = '094e7647f0bdd001e42fd2a73ba802c995af20aa';
const CSV_PATH = 'samples/legacy_support/All_ID_Mapping_Latest.csv';
const RAW = `https://raw.githubusercontent.com/Esri/joint-military-symbology-xml/${COMMIT}/${CSV_PATH}`;

const response = await fetch(RAW);
if (!response.ok) throw new Error(`${RAW}: HTTP ${response.status}`);
const csv = await response.text();

// Columns: id, Name, LegacyKey, MainIcon, Modifier1, Modifier2, ExtraIcon,
// FullFrame, GeometryType, Standard, Status, Notes. No quoted commas in
// the rows we read.
const lines = csv.split(/\r?\n/).filter(Boolean);
const header = lines[0].split(',');
const col = (name) => {
    const i = header.indexOf(name);
    if (i < 0) throw new Error(`column ${name} missing — the upstream table changed shape`);
    return i;
};
const KEY = col('LegacyKey'), ICON = col('MainIcon'), STANDARD = col('Standard'), NAME = col('Name');

const map = new Map();
let rows = 0;
for (const line of lines.slice(1)) {
    const r = line.split(',');
    const key = r[KEY] || '';
    // `G-G-GLP---`: scheme, (affiliation), category, (status), function id.
    if (!/^G-[A-Z]-[A-Z-]{6}$/.test(key)) continue;
    const icon = r[ICON] || '';
    if (!/^25\d{6}$/.test(icon)) continue;
    rows++;
    const compact = key[2] + key.slice(4, 10);
    const standard = r[STANDARD] || '';
    const prev = map.get(compact);
    if (prev && prev.entity !== icon.slice(2)) {
        throw new Error(`${compact} maps to both ${prev.entity} and ${icon.slice(2)} (${r[NAME]})`);
    }
    // A row for 2525C wins over one for 2525B change 2 when both exist.
    if (!prev || (prev.standard === 'B2' && standard !== 'B2')) {
        map.set(compact, { entity: icon.slice(2), standard, name: r[NAME] });
    }
}

const sorted = [...map.keys()].sort();
const out = {
    source: `${REPO_URL}/blob/${COMMIT}/${CSV_PATH}`,
    license: 'Apache-2.0 (Esri joint-military-symbology-xml)',
    note: 'MIL-STD-2525C tactical graphics (scheme G) to their MIL-STD-2525D control-measure entity: key = category letter + function id (positions 3 and 5-10 of the fifteen-letter code), value = the six-digit entity code in symbol set 25. Built by scripts/vendor/mil-2525c-graphics.mjs.',
    map: Object.fromEntries(sorted.map((k) => [k, map.get(k).entity])),
};
writeFileSync(OUT, JSON.stringify(out, null, 1).replace(/\n  "([A-Z-]{7})": "(\d{6})"/g, '\n  "$1": "$2"') + '\n');
console.log(`${rows} tactical-graphic rows read, ${map.size} keys written to ${OUT}`);
