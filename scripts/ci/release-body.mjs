#!/usr/bin/env node
// The body a release is published with: the curated notes, headed by the title of that delivery.
//
// Three things were wrong with the way this used to read on the Releases page.
//
//   The title said the section, not the delivery. It was built as `<tag> — <milestone theme>`,
//   and a milestone is a section now: v2.8.0 and v2.8.1 come out of the same one and would carry
//   the same words. The notes file already names the delivery in its front-matter — "A map you
//   can read, and a gRPC plugin that no longer needs reflection" against the theme "Geospatial
//   map: trajectories, playback & entity grouping" — and that title was published nowhere.
//
//   The list could not show it anyway. GitHub truncates the release name hard, so the theme was
//   half-legible at best; the body has room for a sentence and is where a reader looks.
//
//   The body started at `##`. With no heading above them the sections were the top level, which
//   is one level lower than the document they open. The delivery's title is the `#` they belong
//   under, so the levels below it stay as they are.
//
// The release name is then just the version — `2.7.0` — which is the one thing the list has
// room for and the one thing it must show.
//
// Usage: node scripts/ci/release-body.mjs <notes-file>          the body, headed by its title
//        node scripts/ci/release-body.mjs --title <notes-file>  the title alone, or nothing
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

/**
 * The body without its YAML front-matter, and without the blank line the block leaves behind.
 * Markdown has no use for leading space either way, and the publisher would otherwise open the
 * release with it.
 */
export function stripFrontMatter(text) {
  const s = String(text ?? '');
  if (!s.startsWith('---')) return s.replace(/^\s+/, '');
  const end = s.indexOf('\n---', 3);
  // An unterminated block is a broken file. Swallowing the rest of it would publish an empty
  // release and read as notes nobody wrote, so it comes back untouched and visibly wrong.
  if (end < 0) return s;
  return s.slice(s.indexOf('\n', end + 1) + 1).replace(/^\s+/, '');
}

/**
 * A title that says nothing a reader could not read off the tag: the template placeholder, the
 * bare version, "Bowire v2.6.0". Published as a heading these are noise, and v2.6.0 shipped with
 * exactly that in its front-matter — so this is a state to catch, not to render.
 */
export function isPlaceholderTitle(title) {
  const t = String(title ?? '').trim();
  if (!t) return true;
  if (t.startsWith('<')) return true;                       // <fill in before the tag>
  return /^(bowire\s+)?v?\d+\.\d+(\.\d+)?$/i.test(t);       // 2.6.0 / v2.6.0 / Bowire v2.6.0
}

/** The `title:` from the notes' front-matter, or null when there is none worth printing. */
export function frontMatterTitle(text) {
  const s = String(text ?? '');
  if (!s.startsWith('---')) return null;
  const end = s.indexOf('\n---', 3);
  if (end < 0) return null;
  const line = s.slice(0, end).split('\n').find(l => /^title:\s*/.test(l));
  if (!line) return null;
  const value = line.replace(/^title:\s*/, '').trim().replace(/^["']|["']$/g, '');
  return isPlaceholderTitle(value) ? null : value;
}

/**
 * The published body: the delivery's title as the one `#`, then the curated markdown. A body that
 * already opens with its own `#` is left alone — the file is then the author's to arrange, and a
 * second heading above the first would be two titles.
 */
export function compose(text) {
  const body = stripFrontMatter(text);
  const title = frontMatterTitle(text);
  if (!title) return body;
  if (/^#\s/.test(body)) return body;
  return `# ${title}\n\n${body}`;
}

/** The release name: the version alone, because that is what the Releases list can show. */
export const releaseName = tag => String(tag ?? '').replace(/^v/, '');

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const args = process.argv.slice(2);
  const titleOnly = args[0] === '--title';
  const file = titleOnly ? args[1] : args[0];
  if (!file) {
    console.error('usage: release-body.mjs [--title] <notes-file>');
    process.exit(2);
  }
  const text = readFileSync(file, 'utf8');
  process.stdout.write(titleOnly ? (frontMatterTitle(text) ?? '') : compose(text));
}
