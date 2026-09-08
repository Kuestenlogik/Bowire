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

## Placeholders

Placeholders are single braces: `{url}`, `{count}`, `{protocol}`. Keep them exactly as they appear in English &mdash; the name is what the code passes, so a renamed placeholder renders as literal text.

**Double braces are not placeholders.** `{{token}}` is Bowire's own variable syntax, and some UI strings contain it on purpose &mdash; the header-library value hint reads *"{{token}} resolves against the active environment"*. Leave those alone; the substitution deliberately skips them.

Word order is yours. `"No {protocol} services found"` becomes `"Keine {protocol}-Dienste gefunden"`; the placeholder can sit anywhere in the sentence.

## How a language is chosen

1. The operator's choice in **Settings &rarr; General &rarr; Language**, if they made one.
2. Otherwise the browser's language. A browser asking for `de-AT` gets `de` when there is no `de-AT` catalogue &mdash; a regional variant is closer to its base language than to English.
3. Otherwise English.

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

Prefer one key with a placeholder over string concatenation. `'No ' + name + ' services found'` cannot be translated into a language that orders those words differently.

Adding a key to `en.json` and not to the others is fine &mdash; the parity test only fails on keys a translation *has* that English does not, and on empty values in keys it does have. Translators catch up afterwards.

## What is still English

The sweep is in progress. `landing.js` reads entirely from the catalogue; the rest of the workbench still carries its text inline and is being moved surface by surface &mdash; see [#117](https://github.com/Kuestenlogik/Bowire/issues/117) for what is done and what is left.

Until a surface is swept, it renders in English regardless of the chosen language. Switching to German today changes the landing page and the language selector itself, and leaves everything else as it was.
