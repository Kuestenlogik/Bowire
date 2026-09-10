// #117 — finding the prose that is still hard-coded in a fragment.
//
// Twice now the sweep looked finished and wasn't, and both times for the same
// reason: the search defined the boundary instead of the files doing it. The
// first miss was directories — the guards scanned the core project, which is
// exactly the set the sweep had walked, so 600 strings in the sibling packages
// were invisible. The second miss was slots — the search knew textContent,
// title, placeholder, label and aria-label, so `headline:` and `body:` of every
// empty-state card, and every label handed to a helper as a positional
// argument, went unseen. Those are the most visible sentences in the product.
//
// So this module is the one place that decides what counts as an untranslated
// string, and both the guard test and the reporting script read it. Widening
// the detector here widens it everywhere at once, and the ratchet in
// untranslated-baseline.json turns the widening into visible work rather than
// a silent pass.

import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, sep } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
export const SRC = resolve(__dirname, '../../../src');

/** Every wwwroot/js directory that feeds the bundle, core and siblings alike. */
export function fragmentDirs() {
    return readdirSync(SRC, { withFileTypes: true })
        .filter((e) => e.isDirectory() && e.name.startsWith('Kuestenlogik.Bowire'))
        .map((e) => resolve(SRC, e.name, 'wwwroot', 'js'))
        .filter((dir) => existsSync(dir));
}

/** Every fragment, labelled by its path relative to src/ so failures say where. */
export function fragmentSources() {
    const out = [];
    for (const dir of fragmentDirs()) {
        for (const entry of readdirSync(dir, { withFileTypes: true, recursive: true })) {
            if (!entry.isFile() || !entry.name.endsWith('.js')) continue;
            // _locales.js is generated from the catalogues; i18n.js declares t().
            if (entry.name === '_locales.js' || entry.name === 'i18n.js') continue;
            const path = resolve(entry.parentPath ?? dir, entry.name);
            out.push({
                file: path.slice(SRC.length + 1).split(sep).join('/'),
                text: readFileSync(path, 'utf8'),
            });
        }
    }
    return out;
}

// A single-quoted JS string, escapes included. Fragments are vanilla ES5-ish
// and quote UI text with ' throughout; a double-quoted string in this codebase
// is almost always JSON or an attribute selector.
const STR = String.raw`'((?:[^'\\]|\\.)*)'`;

// The named slots. Each of these puts its value in front of a person.
const SLOT_NAMES = [
    'textContent', 'title', 'placeholder', 'label', 'headline', 'body', 'hint',
    'detail', 'details', 'message', 'description', 'subtitle', 'caption',
    'heading', 'summary', 'note', 'legend', 'prompt', 'tooltip', 'emptyText',
    'emptyLabel', 'helpText', 'errorText', 'okText', 'confirmText', 'cancelText',
    'confirmLabel', 'actionLabel', 'buttonLabel', 'linkLabel', 'ariaLabel',
    'deleteTitle',
    // `status` and `meta` joined the list when the Flows sweep found eleven
    // sentences under `status:` — the line a flow node shows after it runs —
    // and the dropdown meta-chip turned out to be a display slot too. Both
    // also carry protocol values ('OK', 'NOT_FOUND') and internal states
    // ('idle'), which the rejections below already drop.
    'status', 'meta',
].join('|');

// The four detectors, in the order they were learnt.
const PATTERNS = [
    // name: 'text' — the named slots, plus the quoted 'aria-label' form.
    new RegExp(String.raw`(?<![A-Za-z0-9_$])(?:${SLOT_NAMES})\s*:\s*${STR}`, 'g'),
    new RegExp(String.raw`['"]aria-label['"]\s*:\s*${STR}`, 'g'),
    // toast('…'), bowireConfirm('…'), bowirePrompt('…'), alert('…').
    new RegExp(
        String.raw`(?<![A-Za-z0-9_$.])(?:toast|bowireConfirm|bowirePrompt|alert)\(\s*${STR}`, 'g'),
    // A module-private helper taking its label first: _fieldRow('Path pattern', …),
    // _interceptMetaCell('Latency', …). This is the family the slot list missed.
    new RegExp(String.raw`(?<![A-Za-z0-9_$.])_[A-Za-z][A-Za-z0-9_$]*\(\s*${STR}`, 'g'),
    // cond ? 'this' : 'that' — a sentence chosen at run time.
    new RegExp(String.raw`\?\s*${STR}\s*:\s*${STR}`, 'g'),
];

// Words that name something outside Bowire's own text, and so are not Bowire's
// to translate: HTTP verbs and schemes, protocol and format names, status
// badges that sit in the same column as a numeric code.
const VOCABULARY = new Set([
    'GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS', 'TRACE', 'CONNECT',
    'MOCK', 'ERR', 'OK', 'http', 'https', 'ws', 'wss', 'grpc', 'graphql', 'mqtt',
    'sse', 'json', 'xml', 'yaml', 'html', 'text', 'form', 'base64', 'utf-8',
    'GraphQL', 'gRPC', 'MQTT', 'SSE', 'JSON', 'XML', 'YAML', 'HTML',
]);

// The deliberate exception, written where it applies rather than in a list
// somewhere else. A line ending in `// i18n-exempt: why` is skipped, and the
// reason sits next to the string it excuses — so a reviewer can disagree with
// it, which a central allow-list never lets them do.
const EXEMPT = /\/\/\s*i18n-exempt\b/;

/**
 * Is this literal prose a translator should own?
 *
 * The rejections are as much a part of the definition as the acceptances,
 * because a detector that flags every string is a detector nobody runs.
 */
export function looksLikeProse(raw) {
    if (typeof raw !== 'string' || raw.length < 2) return false;
    // The scan reads source text, so a character written as an escape arrives
    // as the escape: '▾' looks like the letters u25BE and sailed through
    // the "two adjacent letters" test as prose. Decode first, and a bare glyph
    // is a bare glyph again — while '▣ Show list' stays the sentence it is.
    const s = raw.replace(/\\u([0-9a-fA-F]{4})/g,
        (_, hex) => String.fromCharCode(parseInt(hex, 16)));
    if (s.length < 2) return false;
    if (VOCABULARY.has(s)) return false;
    // A string padded with a space is one of two very different things: a
    // class-name fragment glued onto a base class (`'bowire-row' + (on ?
    // ' active' : '')`) or a piece of a sentence glued around a value
    // (`'Correlating ' + n + ' steps…'`). The first is not prose; the second
    // is the worst kind of prose there is, because the word order lives in
    // the code where no translator can reach it.
    //
    // What separates them is what is left after the padding: a class fragment
    // is a bare lowercase or kebab token, a sentence fragment carries a
    // capital, a space or punctuation. Rejecting every padded string — which
    // this did at first — waved real sentences through. The bare plural nouns
    // (' step' / ' steps') fall on the class-fragment side and stay out; they
    // are #688's, and counting them here would only make that ticket's work
    // look like this one's.
    if (s !== s.trim() && /^[a-z0-9]+([-_][a-z0-9]+)*$/.test(s.trim())) return false;
    // A URL, a path, a query string, a selector, a template, a tag, a format
    // string. The leading character is enough to tell all of them from prose.
    if (/^(https?:|\/|\.|#|\{|<|%|\?|&)/.test(s)) return false;
    // A CSS value or declaration: translateX(16px), transform:translateX(0).
    if (/^[A-Za-z-]+\(/.test(s) || /^[a-z-]+:[^\s]/.test(s)) return false;
    // Every CSS class in this codebase is prefixed `bowire-`, so a string that
    // starts with it is markup, even when it carries spaces because two
    // classes were written together.
    if (s.startsWith('bowire-')) return false;
    // kebab-case and snake_case identifiers: class names, ids, event names.
    if (/^[a-z0-9]+([-_][a-z0-9]+)*$/.test(s)) return false;
    // A dotted or camelCase identifier with no space and no initial capital.
    if (/^[A-Za-z0-9_$.]+$/.test(s) && !/\s/.test(s) && /^[a-z]/.test(s)) return false;
    // An all-caps token with no space: a badge, a verb, an enum name.
    if (/^[A-Z0-9_]+$/.test(s)) return false;
    // Needs two adjacent letters somewhere to be words rather than punctuation.
    return /[A-Za-z]{2}/.test(s);
}

/**
 * Count the untranslated prose literals per fragment.
 *
 * @param {{file: string, text: string}[]} [sources]
 * @returns {Record<string, number>} file → count, only for files with any.
 */
export function untranslatedCounts(sources = fragmentSources()) {
    const counts = {};
    for (const { file, text } of sources) {
        let n = 0;
        for (const line of text.split('\n')) {
            const trimmed = line.trimStart();
            // Comments explain the code; they are not shipped to anyone.
            if (trimmed.startsWith('//') || trimmed.startsWith('*')) continue;
            if (EXEMPT.test(line)) continue;
            for (const pattern of PATTERNS) {
                pattern.lastIndex = 0;
                for (const m of line.matchAll(pattern)) {
                    for (const group of m.slice(1)) {
                        if (group !== undefined && looksLikeProse(group)) n++;
                    }
                }
            }
        }
        if (n > 0) counts[file] = n;
    }
    return counts;
}

/** The same scan, but keeping the line and the text — for the report. */
export function untranslatedSites(sources = fragmentSources()) {
    const out = [];
    for (const { file, text } of sources) {
        text.split('\n').forEach((line, i) => {
            const trimmed = line.trimStart();
            if (trimmed.startsWith('//') || trimmed.startsWith('*')) return;
            if (EXEMPT.test(line)) return;
            for (const pattern of PATTERNS) {
                pattern.lastIndex = 0;
                for (const m of line.matchAll(pattern)) {
                    for (const group of m.slice(1)) {
                        if (group !== undefined && looksLikeProse(group)) {
                            out.push({ file, line: i + 1, text: group });
                        }
                    }
                }
            }
        });
    }
    return out;
}
