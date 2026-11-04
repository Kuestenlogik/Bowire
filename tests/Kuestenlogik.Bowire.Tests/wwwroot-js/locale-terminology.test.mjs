// #980 - technical terms read the way developers say them. The German
// catalogue had translated them word by word ("Stellvertreter" for proxy,
// "Attrappenwirt" for mock server, "Beiwagen" for sidecar), which a German
// developer reads as an error and has to translate back. The glossary in
// docs/contributing/translations.md ("Technical terms in German") says what
// to use instead; this test holds the forms it rejects out of the catalogue.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const LOCALES = resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/locales');

// Values only: every entry sits on one line as `"key": "value",`.
function values(file) {
    const text = readFileSync(resolve(LOCALES, file), 'utf8');
    return [...text.matchAll(/^\s*"([^"]+)":\s*"(.*)",?\s*$/gm)].map(m => ({ key: m[1], value: m[2] }));
}

// The rejected column of the glossary, as whole words (with inflections and
// as the start or end of a compound where German builds one).
const REJECTED = {
    de: [
        ['Stellvertreter', /stellvertreter/i],
        ['gegenläufig', /gegenläufig/i],
        ['Attrappe', /attrappe/i],
        ['Wirt (host)', /\bwirt(e|s|es|en)?\b|wirts\b|attrappenwirt/i],
        ['Beiwagen', /beiwagen/i],
        ['Beipack', /beipack/i],
        ['Anschluss (port)', /\banschl(uss|üsse|usses)\b(?!\s+an\b)/i],
        ['Strom (stream, flow)', /\bstr(om|öme|oms|ömen)\b|ereignisstrom|strom-/i],
        ['Schiene (rail)', /schiene/i],
        ['Reiter (tab)', /\b(unter)?reiter\b/i],
        ['Kopfzeile (header)', /kopfzeile/i],
        ['Rumpf (body)', /\brumpf/i],
        ['Nutzlast (payload)', /nutzlast/i],
        ['Merkmal (token)', /sitzungsmerkmal|\bmerkmal\b/i],
        ['Keks (cookie)', /\bkeks|kekse/i],
        ['Nabe (hub)', /\bnabe\b/i],
        ['Spinne (spider)', /\bspinne\b|durchkämm/i],
        ['Abklopfen / Verwirrspiel (fuzzing)', /abklopf|verwirrspiel/i],
        ['Sonde (probe)', /\bsonde(n)?\b|sondendatei|sondenergebnis/i],
        ['Prüfstand (benchmark)', /prüfstand/i],
        ['Baustein (widget)', /baustein/i],
        // The noun only: the verb "bündeln" is ordinary German.
        ['Bündel (bundle)', /\bBündel(n|s)?\b/],
        ['Abzug (snapshot)', /\babz(ug|üge|uges)\b/i],
        ['Verarbeitungskette (pipeline)', /verarbeitungskette/i],
        ['Zusatzpaket (plugin)', /zusatzpaket/i],
        ['Zusicherung (assertion)', /zusicherung/i],
        ['Zeugnis (certificate)', /zeugnis/i],
        ['Handschlag (handshake)', /handschlag/i],
        ['Grundadresse (base URL)', /grundadresse/i],
        ['aufgefangen (captured)', /aufgefangen/i],
        ['Ausklappmenü (dropdown)', /ausklappmenü/i],
        ['Zwischenspeicher (cache)', /zwischenspeicher/i],
        // The rail keeps the verb "Erkunden"; the process is "Discovery".
        ['Erkundung (discovery)', /erkundung/i],
        ['Werkbank (workbench)', /werkbank/i],
    ],
};

for (const [locale, rejected] of Object.entries(REJECTED)) {
    test(`${locale}.json uses technical terms the way developers say them (#980)`, () => {
        const hits = [];
        for (const { key, value } of values(`${locale}.json`)) {
            for (const [term, pattern] of rejected) {
                if (pattern.test(value)) hits.push(`${key}: ${term} in "${value}"`);
            }
        }
        assert.deepEqual(hits, [],
            'see "Technical terms in German" in docs/contributing/translations.md for the term to use');
    });
}
