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

/**
 * The packages whose JS does NOT get spliced into the workbench IIFE.
 *
 * BowireHtmlGenerator.IsRailLikeAssembly is the authority: every Bowire-
 * namespaced assembly contributes its `.wwwroot.js.*.js` resources to the
 * bundle except the ones named there. Map is on that list because its widget
 * loads through the extension-asset endpoint as its own <script> — which is
 * why the core's one-letter `t` is not in its scope, and why calling it bare
 * threw on the first map mount.
 *
 * Read out of the C# rather than repeated here, so the two cannot drift: add a
 * package to that exclusion list and this guard follows it the same day.
 */
export function outsideIifePackages() {
    const gen = resolve(SRC, 'Kuestenlogik.Bowire', 'BowireHtmlGenerator.cs');
    const text = readFileSync(gen, 'utf8');
    const body = text.slice(text.indexOf('IsRailLikeAssembly'));
    const end = body.indexOf('CollectRailJsPayload');
    const scope = end < 0 ? body : body.slice(0, end);
    const names = [...scope.matchAll(/string\.Equals\(name,\s*"([^"]+)"/g)]
        .map((m) => m[1])
        // Core and Tool are excluded for a different reason — core's fragments
        // ship inline, Tool has none. Neither has a wwwroot/js of its own that
        // loads separately, so neither is a candidate here.
        .filter((n) => n !== 'Kuestenlogik.Bowire' && n !== 'Kuestenlogik.Bowire.Tool');
    if (names.length === 0) {
        throw new Error('no exclusions parsed from IsRailLikeAssembly — has it moved?');
    }
    return names;
}

/** Every wwwroot/js directory that feeds the bundle, core and siblings alike. */
export function fragmentDirs() {
    return readdirSync(SRC, { withFileTypes: true })
        .filter((e) => e.isDirectory() && e.name.startsWith('Kuestenlogik.Bowire'))
        .map((e) => resolve(SRC, e.name, 'wwwroot', 'js'))
        .filter((dir) => existsSync(dir));
}

/**
 * Every fragment, labelled by its path relative to src/ so failures say where,
 * and by whether it shares the workbench IIFE — the one thing that decides
 * where its `t` comes from.
 */
export function fragmentSources() {
    const outside = new Set(outsideIifePackages());
    const out = [];
    for (const dir of fragmentDirs()) {
        const pkg = dir.slice(SRC.length + 1).split(sep)[0];
        for (const entry of readdirSync(dir, { withFileTypes: true, recursive: true })) {
            if (!entry.isFile() || !entry.name.endsWith('.js')) continue;
            // _locales.js is generated from the catalogues; i18n.js declares t().
            if (entry.name === '_locales.js' || entry.name === 'i18n.js') continue;
            const path = resolve(entry.parentPath ?? dir, entry.name);
            out.push({
                file: path.slice(SRC.length + 1).split(sep).join('/'),
                text: readFileSync(path, 'utf8'),
                outsideIife: outside.has(pkg),
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
    // `desc` joined them when the shortcuts table turned up untranslated
    // under the pseudo-locale: nine descriptions in a slot whose name is an
    // abbreviation of one already on the list.
    'desc',
].join('|');

// The list above names slots exactly, and that kept missing the ones a caller
// invents: `valuePlaceholder`, `descPlaceholder`, `executeLabel`. Three English
// words sat on Bowire's first screen while the report read zero, because none
// of those names was on any list. So match the *shape* of a display slot as
// well: any property whose name ends in one of these carries text for a person.
const SLOT_SUFFIX = [
    'Placeholder', 'Label', 'Title', 'Text', 'Hint', 'Message', 'Caption',
    'Heading', 'Tooltip', 'Description', 'Summary', 'Body', 'Headline',
].join('|');

// The four detectors, in the order they were learnt.
const PATTERNS = [
    // name: 'text' — the named slots, plus the quoted 'aria-label' form.
    new RegExp(String.raw`(?<![A-Za-z0-9_$])(?:${SLOT_NAMES})\s*:\s*${STR}`, 'g'),
    new RegExp(String.raw`['"]aria-label['"]\s*:\s*${STR}`, 'g'),
    // toast('…'), bowireConfirm('…'), bowirePrompt('…'), alert('…').
    new RegExp(
        String.raw`(?<![A-Za-z0-9_$.])(?:toast|bowireConfirm|bowirePrompt|alert)\(\s*${STR}`, 'g'),
    // Any helper taking its label first: _fieldRow('Path pattern', …),
    // renderSettingsRow('Theme', 'Color scheme for the UI', …), statTile('URLs', …).
    //
    // This used to require a leading underscore, on the theory that a label
    // handed over positionally belongs to a module-private helper. Half the
    // Settings dialog disagreed: renderSettingsRow and renderSettingsToggle
    // carry their label and their description as the first two arguments and
    // are named without one. Match any call; the rejections below drop
    // getElementById('bowire-app') and el('div') and their kind.
    // `new Error('…')` is a diagnostic for whoever reads the stack, not a
    // surface, so the negative lookbehind keeps constructors out.
    new RegExp(
        String.raw`(?<!new\s)(?<![A-Za-z0-9_$.])[A-Za-z_$][A-Za-z0-9_$]*\(\s*${STR}`,
        'g'),
    // …and its second argument, which is where those two helpers put the
    // sentence under the label.
    new RegExp(
        String.raw`(?<![A-Za-z0-9_$.])[A-Za-z_$][A-Za-z0-9_$]*\(\s*${STR}\s*,\s*${STR}`, 'g'),
    // A slot named by its suffix: valuePlaceholder, executeLabel, errorTitle.
    new RegExp(String.raw`(?<![A-Za-z0-9_$])[a-z][A-Za-z0-9_$]*(?:${SLOT_SUFFIX})\s*:\s*${STR}`,
        'g'),
    // return 'Execute'; — a label handed back rather than assigned. The
    // rejections below drop the machine strings this also sees.
    new RegExp(String.raw`(?<![A-Za-z0-9_$.])return\s+${STR}\s*;`, 'g'),
    // A lone branch of a two-way choice, alone on its line:
    //
    //     title: disabled
    //         ? 'Nothing to undo (Ctrl/Cmd+Z)'
    //         : ('Undo: ' + label + ' (Ctrl/Cmd+Z)'),
    //
    // The pair pattern below needs both branches to be plain strings, and the
    // slot patterns need the string to follow the colon with only whitespace
    // between — a `?` in the way defeats both. The topbar's undo, redo and
    // trash tooltips sat in English behind exactly that shape while the report
    // read zero. A line that begins with ? or : and a quoted string is a value
    // being chosen; the rejections below drop the machine ones.
    new RegExp(String.raw`^[ 	]*[?:]\s*${STR}`, 'gm'),
    // The true branch of a two-way choice, wherever it sits:
    //
    //     'aria-label': disabled ? 'Undo (nothing to undo)' : ('Undo: ' + label),
    //
    // One line, so the rule above does not apply; a condition between the
    // colon and the string, so the slot rules do not either; and only one
    // branch is a plain string, so the pair rule does not. The topbar's undo
    // and redo aria-labels sat in English behind exactly this. The optional
    // bracket catches `? ('Workspace: ' + name)`.
    // The lookbehind keeps the `?` inside a string literal out: `return '?';`
    // would otherwise start a match at that character and read to the next
    // quote anywhere in the file.
    new RegExp(String.raw`(?<!['"])\?\s*\(?\s*${STR}`, 'g'),
    // cond ? 'this' : 'that' — a sentence chosen at run time.
    new RegExp(String.raw`\?\s*${STR}\s*:\s*${STR}`, 'g'),
    // node.textContent = 'text' — the same slots, written as an assignment
    // rather than in an object literal. Found when a 'None' button in the
    // semantics menu survived a sweep that had walked the whole file.
    new RegExp(String.raw`\.(?:${SLOT_NAMES})\s*=\s*${STR}`, 'g'),
    new RegExp(String.raw`setAttribute\(\s*['"](?:title|aria-label|placeholder)['"]\s*,\s*${STR}`,
        'g'),
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
    // Hyphens belong in here: a catalogue key like tour.add-url.body is an
    // identifier, and leaving them out made every t('tour.*') call read as
    // prose the moment the scan started looking at call arguments.
    if (/^[A-Za-z0-9_$.-]+$/.test(s) && !/\s/.test(s) && /^[a-z]/.test(s)) return false;
    // An all-caps token with no space: a badge, a verb, an enum name.
    if (/^[A-Z0-9_]+$/.test(s)) return false;
    // Needs two adjacent letters somewhere to be words rather than punctuation.
    return /[A-Za-z]{2}/.test(s);
}

// A display slot OPENING an expression: `title:`, `'aria-label':`,
// `node.textContent =`, `executeLabel:`. The concatenation detector asks this
// of the expression it walked back to, not of the line.
const SLOT_HEAD = new RegExp(String.raw`^\s*(?:['"]?(?:${SLOT_NAMES}|aria-label)['"]?`
    + String.raw`|[A-Za-z0-9_$]*(?:${SLOT_SUFFIX})`
    + String.raw`|[A-Za-z0-9_$.]*\.(?:${SLOT_NAMES}))\s*[:=](?!=)`);

/**
 * Every single-quoted string in a source, with its bounds — skipping comments,
 * double-quoted strings, templates and regex literals, so a quote that is not
 * a quote does not shift everything after it.
 *
 * Without this the detector reads `className: 'x', textContent: '+'` as a
 * string containing ", textContent: " — the run between the CLOSING quote of
 * one literal and the OPENING quote of the next. A regex has no way to tell
 * those apart; a scanner does, because it has been counting since the top of
 * the file.
 */
function singleQuoted(text) {
    const out = [];
    let prev = '\n';
    let i = 0;
    while (i < text.length) {
        const c = text[i];
        if (c === '/' && text[i + 1] === '/') {
            while (i < text.length && text[i] !== '\n') i++;
            continue;
        }
        if (c === '/' && text[i + 1] === '*') {
            const end = text.indexOf('*/', i + 2);
            i = end < 0 ? text.length : end + 2;
            continue;
        }
        if (c === '/' && '(,=:[!&|?{};\n'.includes(prev)) {
            i++;                                        // regex literal
            let inClass = false;
            while (i < text.length && text[i] !== '\n') {
                if (text[i] === '\\') { i += 2; continue; }
                if (text[i] === '[') inClass = true;
                else if (text[i] === ']') inClass = false;
                if (text[i] === '/' && !inClass) { i++; break; }
                i++;
            }
            prev = '/';
            continue;
        }
        if (c === '"' || c === '`') {
            const quote = c;
            i++;
            while (i < text.length) {
                if (text[i] === '\\') { i += 2; continue; }
                if (text[i] === '\n' && quote === '"') break;
                if (text[i] === quote) { i++; break; }
                i++;
            }
            prev = quote;
            continue;
        }
        if (c === "'") {
            const start = i;
            let value = '';
            i++;
            while (i < text.length) {
                if (text[i] === '\\') { value += text.slice(i, i + 2); i += 2; continue; }
                if (text[i] === '\n') break;             // unterminated — give up
                if (text[i] === "'") { i++; break; }
                value += text[i++];
            }
            out.push({ value, start, end: i });
            prev = "'";
            continue;
        }
        if (!/\s/.test(c)) prev = c;
        i++;
    }
    return out;
}

/**
 * Prose spliced into a sentence with `+`, inside something that displays it.
 *
 * When a sentence is built around a value there is no literal after `title:`
 * to match, so every slot pattern above looks straight past
 *
 *     title: subs.length + ' active subscription' + … + ' — click for details',
 *
 * and four tooltips kept an English clause through the whole sweep. One read
 * "Arbeitsbereiche sortieren: Anlagedatum — click for Zuletzt benutzt".
 *
 * Two questions per literal, and both are needed. Does it hang off a `+`?
 * Without that the detector reads every `'Bearer ' + token` in the codebase.
 * And does a display slot open the expression it sits in? Asking that of the
 * line instead of the expression lets `style: 'width:' + pct + '%', title: …`
 * through on the strength of a `title:` further along the same line.
 */
function concatSites(text) {
    /**
     * The expression a literal belongs to: walk back past balanced brackets to
     * the punctuation that opened it, and return what stands between.
     */
    const expressionHead = (from) => {
        let depth = 0;
        let i = from - 1;
        for (; i >= 0; i--) {
            const c = text[i];
            if (c === ')' || c === ']' || c === '}') depth++;
            else if (c === '(' || c === '[' || c === '{') {
                if (depth === 0) break;
                depth--;
            } else if (depth === 0 && (c === ',' || c === ';')) break;
        }
        // Blank the strings inside it: their content is not this expression's
        // syntax, and a `,` or `:` in a sentence would end the walk early.
        return text.slice(i + 1, from).replace(/'(?:[^'\\]|\\.)*'/g, "''");
    };

    const out = [];
    for (const lit of singleQuoted(text)) {
        // Hanging off a `+` in either direction is what makes it a fragment of
        // a sentence rather than the whole of one.
        if (!/^\s*\+/.test(text.slice(lit.end, lit.end + 8))
            && !/\+\s*$/.test(text.slice(Math.max(0, lit.start - 8), lit.start))) continue;
        const head = expressionHead(lit.start);
        if (!SLOT_HEAD.test(head)) continue;
        // A key assembled from pieces — `t('headerLibrary.row.' + field + …)`.
        // The literal is part of a catalogue key, not something anyone reads.
        if (/(?<![A-Za-z0-9_$.])t\(\s*''\s*\+/.test(head)) continue;
        out.push({ group: lit.value, offset: lit.start });
    }
    return out;
}

/**
 * Every prose literal in one fragment, with the line it starts on.
 *
 * The scan runs over the whole file rather than line by line. It used to go
 * line by line, which is simpler and was wrong in a way that stayed hidden:
 * `title:` at the end of one line and its string on the next never matched,
 * so the topbar's "Nothing to undo (Ctrl/Cmd+Z)" was invisible to a report
 * that said zero while the browser showed it. Matching across the newline
 * finds it; the line number comes from the match offset.
 */
function scan({ file, text }) {
    const lineStarts = [0];
    for (let i = 0; i < text.length; i++) {
        if (text[i] === '\n') lineStarts.push(i + 1);
    }
    const lineOf = (offset) => {
        let lo = 0;
        let hi = lineStarts.length - 1;
        while (lo < hi) {
            const mid = (lo + hi + 1) >> 1;
            if (lineStarts[mid] <= offset) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    };
    const lines = text.split('\n');

    // Both detectors hand over the same thing — a candidate string and where
    // it sits — so the rejections, the comment skip and the exemption comment
    // are applied once, below.
    const found = [];
    for (const pattern of PATTERNS) {
        pattern.lastIndex = 0;
        for (const m of text.matchAll(pattern)) {
            for (let g = 1; g < m.length; g++) {
                if (m[g] === undefined) continue;
                // The string's own offset, not the match's: a multi-line match
                // must be blamed on the line the text sits on, so a reader can
                // go straight there and an // i18n-exempt beside it counts.
                const at = m.index + m[0].indexOf(`'${m[g]}'`);
                found.push({ group: m[g], offset: at < m.index ? m.index : at });
            }
        }
    }
    found.push(...concatSites(text));

    const out = [];
    const seen = new Set();
    for (const { group, offset } of found) {
        if (group === undefined || !looksLikeProse(group)) continue;
        const idx = lineOf(offset);
        const line = lines[idx] ?? '';
        const trimmed = line.trimStart();
        if (trimmed.startsWith('//') || trimmed.startsWith('*')) continue;
        if (EXEMPT.test(line)) continue;
        const key = `${idx}:${group}`;
        if (seen.has(key)) continue;
        seen.add(key);
        out.push({ file, line: idx + 1, text: group });
    }
    out.sort((a, b) => a.line - b.line);
    return out;
}

/**
 * Count the untranslated prose literals per fragment.
 *
 * @param {{file: string, text: string}[]} [sources]
 * @returns {Record<string, number>} file → count, only for files with any.
 */
export function untranslatedCounts(sources = fragmentSources()) {
    const counts = {};
    for (const source of sources) {
        const n = scan(source).length;
        if (n > 0) counts[source.file] = n;
    }
    return counts;
}

/** The same scan, but keeping the line and the text — for the report. */
export function untranslatedSites(sources = fragmentSources()) {
    return sources.flatMap(scan);
}

// ---------------------------------------------------------------------------
// #117 — t() where nobody can reach it a second time.
//
// Every fragment runs inside the one IIFE prologue.js opens and declares no
// function of its own around its tables, so a `t(...)` that no function
// encloses is evaluated exactly once: when the bundle loads. setLocale swaps
// the active catalogue without reloading the page, so whatever that call
// resolved to is the boot language, frozen for the rest of the session.
//
// Seven select-option tables were built that way — sort modes, console time
// filters, mock rule operators, auth schemes, fault kinds and distributions.
// Each read correctly in whichever language the workbench started in, which is
// why nineteen frozen labels survived the whole sweep unnoticed: nothing shows
// them wrong until someone changes language mid-session, and the pseudo-locale
// is set before the reload that installs it.
//
// The measure is FUNCTION depth, not brace depth. `var OPTS = [{ label: t(…) }]`
// sits two braces deep and still runs at load; the fix,
// `get label() { return t(…); }`, adds no brace a reader would notice but puts
// the call behind a function that runs when the label is read.
// ---------------------------------------------------------------------------

/**
 * For every offset in a JS source: how many function bodies enclose it, and
 * whether it is code at all rather than the inside of a string, template,
 * comment or regex literal.
 *
 * Both answers come out of one pass because they are the same walk. The second
 * matters as much as the first — without it the guard reads the `t('` inside
 * the comment that explains why rail labels are NOT built that way, and fails
 * on prose.
 *
 * Telling a function body from an object literal or a plain block is the whole
 * job, and it comes down to what sits before the brace: `=>`, or a `)` whose
 * `(` was not opened by if / for / while / switch / catch. Deliberately a
 * scanner rather than a parser — it answers one question, and has to stay
 * readable for whoever the guard fails on.
 */
function scanFunctionDepths(text) {
    const depth = new Int32Array(text.length + 1);
    const isCode = new Uint8Array(text.length + 1);
    const braces = [];         // one entry per open `{`: is it a function body?
    const parens = [];         // one entry per open `(`: the keyword before it
    const BLOCK_HEADS = new Set(['if', 'for', 'while', 'switch', 'catch', 'with']);
    let funcDepth = 0;
    let prev = '\n';           // last significant char — tells `/` apart
    let lastParenKind = null;  // what the most recently CLOSED `(` belonged to
    let i = 0;
    const mark = (code) => {
        depth[i] = funcDepth;
        isCode[i] = code ? 1 : 0;
        i++;
    };
    /** The identifier immediately before offset `at`, if any. */
    const wordBefore = (at) => {
        let j = at - 1;
        while (j >= 0 && /\s/.test(text[j])) j--;
        let end = j + 1;
        while (j >= 0 && /[A-Za-z0-9_$]/.test(text[j])) j--;
        return text.slice(j + 1, end);
    };

    while (i < text.length) {
        const c = text[i];
        if (c === '/' && text[i + 1] === '/') {
            while (i < text.length && text[i] !== '\n') mark(false);
            continue;
        }
        if (c === '/' && text[i + 1] === '*') {
            const end = text.indexOf('*/', i + 2);
            const stop = end < 0 ? text.length : end + 2;
            while (i < stop) mark(false);
            continue;
        }
        if (c === '"' || c === "'" || c === '`') {
            mark(false);
            while (i < text.length) {
                if (text[i] === '\\') { mark(false); mark(false); continue; }
                // An unterminated quote would otherwise eat the rest of the
                // file; only a template literal legitimately spans lines.
                if (text[i] === '\n' && c !== '`') break;
                const closing = text[i] === c;
                mark(false);
                if (closing) break;
            }
            prev = c;
            continue;
        }
        if (c === '/' && '(,=:[!&|?{};\n'.includes(prev)) {
            mark(false);                              // regex literal
            let inClass = false;
            while (i < text.length && text[i] !== '\n') {
                if (text[i] === '\\') { mark(false); mark(false); continue; }
                if (text[i] === '[') inClass = true;
                else if (text[i] === ']') inClass = false;
                const closing = text[i] === '/' && !inClass;
                mark(false);
                if (closing) break;
            }
            prev = '/';
            continue;
        }
        if (c === '(') {
            parens.push(BLOCK_HEADS.has(wordBefore(i)) ? 'block' : 'params');
        } else if (c === ')') {
            lastParenKind = parens.pop() ?? null;
        } else if (c === '{') {
            // `=>` or a parameter list before the brace makes it a body.
            const body = (prev === '>' && text.lastIndexOf('=', i) >= 0
                    && /=>\s*$/.test(text.slice(Math.max(0, i - 40), i)))
                || (prev === ')' && lastParenKind === 'params');
            braces.push(body);
            if (body) funcDepth++;
        } else if (c === '}') {
            if (braces.pop()) funcDepth--;
        }
        if (!/\s/.test(c)) prev = c;
        mark(true);
    }
    depth[text.length] = funcDepth;
    return { depth, isCode };
}

const T_CALL = /(?<![A-Za-z0-9_$.])t\(\s*['"]/g;

/**
 * Every `t('…')` a fragment resolves at load time, as `file:line: source`.
 *
 * A bundle outside the IIFE is skipped: it brings its own `t` and registers
 * from its own top level, so "no function encloses this" means something else
 * there — map-translation.test.mjs covers that case directly instead.
 */
export function frozenTranslations({ file, text, outsideIife }) {
    if (outsideIife) return [];
    const { depth, isCode } = scanFunctionDepths(text);
    const lines = text.split('\n');
    const starts = [];
    let at = 0;
    for (const line of lines) { starts.push(at); at += line.length + 1; }

    const out = [];
    T_CALL.lastIndex = 0;
    let m;
    while ((m = T_CALL.exec(text)) !== null) {
        if (!isCode[m.index] || depth[m.index] !== 0) continue;
        // Which line the offset landed on.
        let lo = 0;
        let hi = starts.length - 1;
        while (lo < hi) {
            const mid = (lo + hi + 1) >> 1;
            if (starts[mid] <= m.index) lo = mid; else hi = mid - 1;
        }
        out.push(`${file}:${lo + 1}: ${lines[lo].trim().slice(0, 90)}`);
    }
    return out;
}

