---
title: Translating Bowire
summary: 'How the UI catalogues work, and how to add or finish a language without installing .NET.'
---

# Translating Bowire

Bowire's interface reads its text from a catalogue. Adding a language means writing one JSON file &mdash; no .NET SDK, no build, no C#.

## Where the files are

```
src/Kuestenlogik.Bowire/wwwroot/locales/
├── en.json     the source of truth
└── de.json     German
```

Each file is a flat map from a dotted key to the text that key renders:

```json
{
  "landing.noServices.title": "No services discovered yet",
  "landing.wrongProtocol.title": "No {protocol} services found"
}
```

`en.json` is canonical. Every other file carries **exactly** its key set &mdash; no more, no fewer.

## Adding a language

Scaffold the file from the English catalogue:

```bash
bowire docs translations fr
```

That writes `fr.json` with every key, each preceded by a comment showing the English text, and every value empty. Fill in what you can; an empty value falls back to English at runtime, so a half-finished catalogue is usable rather than broken.

Check your progress at any point:

```bash
bowire docs translations fr --check fr.json
```

It reports keys you are missing, keys English does not have, and values still empty, and exits non-zero while anything is outstanding &mdash; so you can gate your own pipeline on it.

When you are happy, drop the file into `wwwroot/locales/` and open a pull request. The build picks up any `*.json` in that directory; nothing needs registering.

## The rules the build enforces

A test fails the build when a catalogue drifts from English:

| Check | Why |
|---|---|
| Same key set | A translation that quietly loses a key falls back to English, and nobody notices until a user reports a half-translated screen. |
| No empty values | An empty value also falls back &mdash; so it looks like a translation exists when none does. Leave the key out while scaffolding, or fill it. |
| Same placeholders | A translation that drops `{url}` renders a sentence with a hole in it; one that invents `{name}` renders a literal brace. Neither shows up in a key-set check. |
| Keys are `area.thing.part` | What lets you work through one surface at a time, and what keeps a flat file navigable at a thousand entries. |
| No key declared twice | JSON has no duplicate keys, and `JSON.parse` takes the last one without a word &mdash; so a second entry does not collide, it deletes the first. Twelve keys had drifted into two entries, six carrying different sentences, and six surfaces were showing another surface's text with every other test passing. Parsing cannot find this, so the guard reads the lines. |

Three more guards watch the code rather than the catalogues. All three are about a translation that *cannot arrive*, which is invisible to a screenshot in the language the workbench happens to be in:

| Check | Why |
|---|---|
| No fragment binds the name `t` | Every fragment shares one IIFE, and the translator is a one-letter name in it &mdash; also the obvious name for a loop counter or a callback parameter. `toast()` held `var t = el('div', …)` with `t('common.dismiss')` inside it, and every toast in Bowire threw until the local was renamed. |
| A bundle outside the IIFE brings its own `t` | The mirror image. The map widget loads from the extension-asset endpoint as its own `<script>`, so the core's `t` is not in scope; its nineteen swept strings threw `ReferenceError` on the first mount. Outside bundles reach the catalogue through `BowireExtensions.t`. Which packages are outside is read out of `BowireHtmlGenerator.IsRailLikeAssembly`, so the two cannot drift. |
| No `t()` resolves at load time | A call no function encloses runs once, when the bundle loads, and `setLocale` does not reload &mdash; so it shows the boot language for the rest of the session. Seven select-option tables were built that way. Write `get label() { return t('sort.name'); }` instead of `label: t('sort.name')`. |

## Placeholders

Placeholders are single braces: `{url}`, `{count}`, `{protocol}`. Keep them exactly as they appear in English &mdash; the name is what the code passes, so a renamed placeholder renders as literal text.

**Double braces are not placeholders.** `{{token}}` is Bowire's own variable syntax, and some UI strings contain it on purpose &mdash; the header-library value hint reads *"{{token}} resolves against the active environment"*. Leave those alone; the substitution deliberately skips them.

Word order is yours. `"No {protocol} services found"` becomes `"Keine {protocol}-Dienste gefunden"`; the placeholder can sit anywhere in the sentence.

## How a language is chosen

**In the workbench:**

1. The operator's choice in **Settings &rarr; General &rarr; Language**, if they made one.
2. Otherwise the browser's language. A browser asking for `de-AT` gets `de` when there is no `de-AT` catalogue &mdash; a regional variant is closer to its base language than to English.
3. Otherwise English.

**On the command line**, where there is no settings dialog to ask:

1. `BOWIRE_LOCALE`, because somebody who sets it has said something about Bowire rather than about their shell.
2. `LC_ALL`, then `LANG` &mdash; how a POSIX shell says it.
3. The operating system's current UI culture.
4. Otherwise English.

A shell value is narrowed step by step: `de_DE.UTF-8@euro` loses the modifier, then the encoding, then the region, so it finds `de.json` without anyone configuring anything. `C` and `POSIX` mean *no locale* and are ignored rather than read as a language named C.

```bash
BOWIRE_LOCALE=de bowire docs translations fr --check fr.json
```

Both surfaces read the **same** files. There is no second catalogue for the CLI, no `.resx`, nothing to keep in step &mdash; a key you translate is translated everywhere it appears.

A missing key renders as the key itself (`landing.noServices.title`) rather than as blank space. That is deliberate: silence would ship unnoticed.

## For contributors touching the UI

New user-visible text goes into `en.json` with a key, and into the code as `t('your.key')`. Never as a literal.

```js
// no
el('h3', { textContent: 'No services discovered yet' })

// yes
el('h3', { textContent: t('landing.noServices.title') })
```

With a value to interpolate:

```js
el('p', { textContent: t('landing.wrongProtocol.title', { protocol: name }) })
```

Prefer one key with a placeholder over string concatenation. `'No ' + name + ' services found'` cannot be translated into a language that orders those words differently &mdash; and the guard rejects it: a prose literal hanging off a `+` inside a display slot counts as untranslated, the same as a bare one.

That rule is worth stating the other way round, because it is the failure it was written for: **a key must be a whole sentence.** Three keys had ended mid-clause with the rest of the sentence sitting in English beside the call:

```js
// no — the translator gets a beginning with no end, and cannot move a word
//      without breaking the join
title: t('env.fromHostTitle') + 'host is configured — edits here would not survive a restart.'

// yes — one key, the whole sentence, word order theirs
title: t('env.fromHostTitle')
```

A value in the middle is what placeholders are for. A tally that changes the wording is what two keys are for (see #688):

```js
// #688 - one message, two shapes.
title: t(count === 1 ? 'main.presetsOne' : 'main.presetsMany', { count: count })
```

Adding a key to `en.json` and not to the others is fine &mdash; the parity test only fails on keys a translation *has* that English does not, and on empty values in keys it does have. Translators catch up afterwards.

## What stays English on purpose

Not everything in the interface is Bowire's to translate. A word that names something *outside* Bowire's own text keeps its name:

| Stays | Why |
|---|---|
| `INVALID_ARGUMENT`, `404 Not Found` | gRPC status names and HTTP reason phrases come from the specification. A German "404 Nicht gefunden" breaks the link to curl, to a browser's network panel, to a colleague's screenshot. |
| Tool, Resource, Prompt | MCP's three primitive kinds. Each maps to a wire call (`tools/call`, `resources/read`, `prompts/get`), and the hint under them names those calls. |
| Bearer Token, Basic Auth, JWT, HMAC, PKCE, mTLS | Names of authentication schemes and algorithms. Somebody searching for one searches for that name. |
| QoS, Retain, `withCredentials`, `Set-Cookie` | Names of protocol settings, flags and headers. |
| `curl`, `grpcurl`, `wscat`, PowerShell | Tools and shells. |
| OpenAPI, Swagger, GeoJSON, WGS84, RFC 7946, ISO&#8209;8601 | Standards and formats. |
| `bowire call`, `dotnet add package`, `{{secret.*}}` | Commands and Bowire's own syntax. |
| `DEPR`, `DEPRECATED` | The schema's own deprecation marker, not Bowire's word. |

The sentence *around* one of these is translated; the thing being named is not. A status bar that reads "Verbunden" beside `INVALID_ARGUMENT` is doing this on purpose: it tells the operator at a glance which half is Bowire talking and which half is the server.

The same reasoning applies to anything Bowire *writes into data*. A workspace name, a collection name, an action-log entry: those travel through `.bww` export into somebody else's Bowire, so a default written in one language would arrive in theirs. Bowire stores them empty or in English and translates only the display.

## The guard that says when the sweep is done

Twice the sweep looked finished and wasn't, both times because the search drew the boundary and the files quietly disagreed. First the guards scanned only `src/Kuestenlogik.Bowire/wwwroot/js` &mdash; the same directory the sweep had walked &mdash; so six hundred literals in the sibling packages were invisible to both. Then the search knew only the slots it had been handed (`textContent`, `title`, `placeholder`, `label`, `aria-label`), so the `headline:` and `body:` of every empty-state card went unseen, along with every label passed to a helper positionally.

So the count of what is still hard-coded is checked in, per file, in `tests/Kuestenlogik.Bowire.Tests/wwwroot-js/untranslated-baseline.json`, and a test holds every file to its number:

```
npm run i18n:report              # what is left, per file
npm run i18n:report flows.js     # the individual sites in one file
npm run i18n:baseline            # record the new, lower numbers
```

Add a literal and the test fails. Remove one and it fails too, telling you to run `npm run i18n:baseline` &mdash; which puts the shrinking number in the same commit as the strings it removed, rather than in somebody's head.

When a string genuinely isn't Bowire's prose, say so where it sits:

```js
el('span', { textContent: 'MOCK' })   // i18n-exempt: sits in the method column beside GET
```

The reason belongs next to the string, not in a list elsewhere, so a reviewer can disagree with it.

## Checking coverage: the pseudo-locale

The ratchet above finds what its patterns were taught to look for, and in the sweep that produced this catalogue it read **zero six times** while the running workbench still showed English. Each gap was a call shape nobody had told the scanner about &mdash; a slot named by a suffix, a lone branch of a ternary, a label handed to a helper positionally, a sentence assembled around a value with `+`, a rail label that arrives from the host rather than from JavaScript at all.

Two gaps it cannot close by widening, because there is nothing on screen to see:

- **A string that throws before it renders.** The map widget's nineteen were swept correctly and every one failed at runtime, because that bundle loads outside the IIFE where `t` lives. The browser console said so; no scanner could.
- **A string that resolves at load.** A `t()` no function encloses is right in whichever language the session started in, and the pseudo-locale is *set* before the reload that installs it. Only a language change mid-session shows the freeze.

Both now have guards of their own (see the table above). The pseudo-locale is for the rest.

A scanner cannot answer "is anything untranslated?". The product can:

```js
localStorage.setItem('bowire_locale', 'qps');
location.reload();
```

Every string that comes *through the catalogue* is then wrapped in `⟦…⟧` with its vowels accented: `Execute` renders as `⟦Ëxëcûtë⟧`. **Anything on screen without the brackets never went through `t()`.** Walk the surface you changed, and the untranslated text announces itself.

Two things survive the marking untouched, because breaking them would break the thing the check exists to verify: `{name}` placeholders, which `interpolate` fills, and `{{name}}`, which is Bowire's own variable syntax and must reach the screen unexpanded.

The accents also lengthen the text by roughly a fifth, so a label that only just fits in English shows its truncation here rather than in the first translation that ships.

`qps` is not offered in the language selector &mdash; picked by accident it would read as a broken install. Set it in `localStorage` as above; `setLocale('auto')` or picking a language in Settings clears it.

What legitimately stays unbracketed: the product's own name and version, the user's own data (workspace and collection names), protocol vocabulary (`GET`, `REST`), keyboard shortcuts, and theme values.

## What is still English

`npm run i18n:report` prints zero. Every fragment that lands in the bundle &mdash; the core project and all eleven sibling packages &mdash; reads its text from the catalogue, and the ratchet in `untranslated-baseline.json` is an empty object, so the next literal anyone adds fails the build.

What remains English is deliberate, and each instance says so on its own line with `// i18n-exempt: <reason>`. There are around 270, and they fall into five groups:

| Group | Why |
|---|---|
| Action-log and console entries | They store rendered text rather than a key, so a language switch would leave a mixed-language history. Fixing that is #689. |
| Run and replay status labels | They are the aggregation key for a run summary *and* are written verbatim into the CSV, k6-summary, OTLP and HTML-report exports. Translating one would change the artefact and break the grouping. |
| Defaults written into data | `Workspace 2`, `New Environment`. These travel out through the `.bww` export into somebody else's Bowire, so a translated default would freeze one language into their workspace. |
| Protocol, product and tool names | QoS, Retain, Bearer Token, OpenAPI, curl, grpcurl, MapLibre, Consul, the whole licence table. Covered by the rule above. |
| Example values and commands | `e.g. baseUrl`, `sk-...`, `dotnet add package …`. A person replaces them or types them; they are not sentences. |

The command line's own `--help` text &mdash; roughly three hundred option and command descriptions &mdash; is tracked separately on #690, which asks first whether it should be translated at all.
