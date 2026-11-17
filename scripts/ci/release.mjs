#!/usr/bin/env node
// Release bookkeeping (docs/contributing/project-board.md). The rules live in release-plan.mjs and
// the repository's values in release.config.json beside it; this is the part that talks to GitHub.
// Shared by every repository that carries the release tool (see release-plan.mjs).
//
// A release is planned, not reconstructed: every ticket names the Release it is planned for (`0.2`)
// and the milestone of that release (`v0.2 — <theme>`). A milestone with nothing open is a release
// that is due, once the releases before it have shipped.
//
//   status                      The open releases, lowest first, with what is still open in each;
//                               which one is due, or what the lowest one still waits for; open
//                               milestones that name no release; and what closed since the last
//                               release without a plan.
//   notes <version>             Drafts docs/release-notes/v<version>.md from the plan: the tickets
//                               planned for this release and done, grouped by area, those that closed
//                               since the last release without a plan, and a section "Upgrading"
//                               linking docs/release-notes/upgrading-from-<previous tag>.md where that
//                               file exists. Never overwrites a draft someone is writing, unless
//                               --force; --out <file> drafts elsewhere; --highlights prints the
//                               grouped ticket list alone and writes nothing.
//   cut <version> [--dry-run]   Fetches, then refuses while HEAD is not origin/main, while a pull
//                               request is open, while a release before it is open, while a ticket
//                               planned for it or an issue in its milestone is open, while the notes
//                               are missing, uncommitted or still a draft, and, where the
//                               configuration says so, while the version file does not carry the
//                               version. Then it claims the tickets that closed without a release,
//                               tags with a message naming the milestone and pushes, publishes the
//                               release (or leaves that to the configured pipeline) and closes the
//                               milestone.
//
// Options: --product <name> (default: the repository's product), --since <tag> (default: the latest
// release tag before this one). Needs the gh CLI signed in, with project scope for the board.
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync, readFileSync, existsSync, mkdtempSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { tmpdir } from 'node:os';
import {
  CONFIG, REPO, NAME, planByField, productOfRepo, planFor, releases, unversioned, milestonesOf, dueRelease,
  complete, releasesBefore, parseMilestone, releaseMatches, releaseOfVersion, fullVersion, nextMinor, notesAreDraft,
  hasSections, previousRelease, versionOfProps, headRefusals, notesPath, upgradeNotesPath, draftNotes, highlights,
  ref, fill,
} from './release-plan.mjs';
import { compose, releaseName } from './release-body.mjs';
import { gh, board, boardItems, milestones, openPullRequests } from './release-board.mjs';

const USAGE = 'usage: release.mjs status | notes <version> [--force] [--out <file>] [--highlights] | cut <version> [--dry-run]   (--product <name>, --since <tag>)';
const [command, ...rest] = process.argv.slice(2);
const valued = new Set(['--since', '--product', '--out']);
const known = new Set(['--force', '--dry-run', '--highlights']);
const flags = new Set(), args = [], opts = new Map();
for (let i = 0; i < rest.length; i++) {
  if (valued.has(rest[i])) opts.set(rest[i], rest[++i]);
  else if (known.has(rest[i])) flags.add(rest[i]);
  else if (rest[i].startsWith('--')) { console.error(`unknown option ${rest[i]}\n${USAGE}`); process.exit(2); }
  else args.push(rest[i]);
}
const git = (...a) => execFileSync('git', a, { encoding: 'utf8' }).trim();
const day = iso => (iso ?? '').slice(0, 10);
const product = opts.get('--product') ?? productOfRepo(REPO);
const own = productOfRepo(REPO);

/** The release tags with their dates. */
function tags() {
  const out = git('for-each-ref', 'refs/tags', '--format=%(refname:short)|%(creatordate:iso-strict)');
  return out ? out.split('\n').map(l => { const [tag, at] = l.split('|'); return { tag, at }; }) : [];
}
/** The release this one follows: --since, or the latest release tag before `tag`. */
function since(tag) {
  const all = tags();
  const named = opts.get('--since');
  if (named) return all.find(t => t.tag === named) ?? (() => { console.error(`no tag ${named}`); process.exit(2); })();
  return previousRelease(tag, all);
}
const version = CONFIG.version;
const propsVersion = (element = version?.elements[0]) =>
  version && existsSync(version.file) ? versionOfProps(readFileSync(version.file, 'utf8'), element) : null;

// ── status ───────────────────────────────────────────────────────────────────
if (command === 'status' && args.length === 0) {
  const items = boardItems();
  const all = milestones();
  const list = releases(all, items, product);
  const last = since(null);
  const current = version ? (propsVersion() ?? `(no version in ${version.file})`) : '(the version comes from the tag)';
  console.log(`${product} ${current} on this branch; last release ${last ? `${last.tag} (${day(last.at)})` : 'none'}.\n`);
  if (list.length === 0) console.log(`No open release of ${product}: open a milestone \`v<next> — <theme>\` and plan the tickets for it.`);
  else {
    const width = Math.max(...list.map(r => r.milestone.title.length));
    for (const r of list) {
      const state = complete(r) ? (r.done > 0 ? 'nothing open' : 'empty') : `${r.open} open`;
      const disagree = r.milestoneOpen !== r.open ? ` · the milestone counts ${r.milestoneOpen} open` : '';
      console.log(`${r.milestone.title.padEnd(width)}  ${state.padStart(12)} · ${r.done} done${disagree}`);
    }
    console.log('');
    const due = dueRelease(list);
    if (due) {
      const prs = openPullRequests();
      console.log(`Release due: ${product} v${due.version} (${due.milestone.title}).${prs.length ? ` ${prs.length} open pull request(s): merge or close them first.` : ''}`);
      console.log(`Next: node scripts/ci/release.mjs notes ${fullVersion(due.version)}`);
    } else {
      const first = list[0];
      const open = items.filter(it => it.product === product && !it.ignored && it.state === 'OPEN' && releaseMatches(it.release, first.version));
      console.log(`No release due: v${first.version} still has ${open.length} open ticket(s)${first.milestoneOpen !== open.length ? ` (${first.milestoneOpen} in its milestone)` : ''}:`);
      for (const it of open.sort((a, b) => a.number - b.number)) console.log(`  ${ref(it)} ${it.title}`);
      for (const r of list.slice(1).filter(r => complete(r) && r.done > 0)) console.log(`v${r.version} has nothing open and waits for the releases before it.`);
    }
  }
  const stray = product === own ? unversioned(all) : [];
  if (stray.length) {
    console.log(`\n${stray.length} open milestone(s) name no release; retitle each \`v<version> — <theme>\`:`);
    for (const m of stray) console.log(`  ${m.title} (${m.open_issues} open)`);
  }
  const { unplanned } = planFor(items, product, '0.0.0', last?.at ?? null);
  if (unplanned.length) {
    console.log(`\n${unplanned.length} ticket(s) closed since ${last.tag} without a release; the next cut claims them:`);
    for (const it of unplanned) console.log(`  ${ref(it)} ${it.title}`);
  }
  process.exit(0);
}

// ── notes / cut ──────────────────────────────────────────────────────────────
if ((command === 'notes' || command === 'cut') && args.length === 1) {
  const full = fullVersion(args[0]);
  if (!full) { console.error(`${args[0]} is no version such as 0.2.0 or 0.2.1\n${USAGE}`); process.exit(2); }
  const tag = `v${full}`;
  const release = releaseOfVersion(full);
  const items = boardItems();
  const all = milestones();
  const prev = since(tag);
  const { shipped, stillOpen, unplanned } = planFor(items, product, full, prev?.at ?? null);
  const ms = milestonesOf(all, product, full);

  for (const it of stillOpen) console.error(`still open, planned for ${release}: ${ref(it)} ${it.title}`);
  if (unplanned.length) console.error(`closed since ${prev?.tag ?? 'the start'} without a release (the cut claims them):\n${unplanned.map(it => `  ${ref(it)} ${it.title}`).join('\n')}`);

  if (command === 'notes') {
    if (flags.has('--highlights')) { process.stdout.write(highlights([...shipped, ...unplanned])); process.exit(0); }
    let commits = null;
    try { if (prev) commits = Number(git('rev-list', '--count', `${prev.tag}..HEAD`)); } catch { /* a shallow clone */ }
    const md = draftNotes({ product, version: full, release, milestones: ms, shipped, unplanned, since: prev?.tag ?? null, commits, upgrading: prev ? existsSync(upgradeNotesPath(prev.tag)) : false });
    const path = opts.get('--out') ?? notesPath(tag);
    mkdirSync(dirname(path), { recursive: true });
    // Created exclusively rather than checked and then written: a draft someone is editing is never
    // overwritten by a race, only by an explicit --force.
    try {
      writeFileSync(path, md, { flag: flags.has('--force') ? 'w' : 'wx' });
    } catch (e) {
      if (e.code !== 'EEXIST') throw e;
      console.log(`${path} exists: edit it, or pass --force to draft it again.`);
      process.exit(0);
    }
    const areas = new Set([...shipped, ...unplanned].map(it => it.area ?? 'other')).size;
    console.log(`→ ${path}: ${shipped.length + unplanned.length} ticket(s) in ${areas} group(s). Write it, commit it, then: node scripts/ci/release.mjs cut ${full} --dry-run`);
    process.exit(0);
  }

  // cut: every reason not to is named before anything is written.
  const refusals = [];
  if (product !== own) refusals.push(`cut tags this repository; ${product} is released by its own pipeline.`);
  git('fetch', '--quiet', 'origin', 'main');
  refusals.push(...headRefusals(git('rev-parse', 'HEAD'), git('rev-parse', 'origin/main'), openPullRequests()));
  for (const r of releasesBefore(releases(all, items, product), full)) refusals.push(`v${r.version} (${r.milestone.title}) is open and ships first.`);
  if (ms.length === 0) refusals.push(`no milestone v${release}: a release nobody planned.`);
  if (stillOpen.length) refusals.push(`${stillOpen.length} ticket(s) planned for ${release} are open: finish them or move them to a later release.`);
  for (const m of ms) {
    const onBoard = stillOpen.filter(it => it.milestone === m.title).length;
    if (m.open_issues > onBoard) refusals.push(`'${m.title}' holds ${m.open_issues - onBoard} open issue(s) not planned for ${release} on the board: plan them or move them to another milestone.`);
  }
  // The notes the release is published with: the first of the accepted files that is curated, as the
  // release pipelines take it. Read, not checked and then read: a file could go between the two.
  const candidates = [notesPath(tag), ...CONFIG.notes.also].map(f => { try { return { f, text: readFileSync(f, 'utf8') }; } catch (e) { if (e.code !== 'ENOENT') throw e; return null; } }).filter(Boolean);
  const notes = candidates.find(c => hasSections(c.text)) ?? candidates[0] ?? null;
  if (!notes) refusals.push(`no ${notesPath(tag)}: run 'notes ${full}' and write it.`);
  else {
    if (notesAreDraft(notes.text)) refusals.push(`${notes.f} is still a draft: give it a title, delete the draft comment, fill every placeholder in angle brackets, and keep at least one ### section.`);
    if (git('status', '--porcelain', '--', notes.f)) refusals.push(`${notes.f} is not committed: the tag carries the notes it was cut with.`);
  }
  if (version?.atCut === 'equal') {
    for (const element of version.elements) {
      const found = propsVersion(element);
      if (found !== full) refusals.push(`<${element}> in ${version.file} is ${found ?? 'missing'}: commit <${element}>${full}</${element}> first (the tag names the version the assemblies carry).`);
    }
  }
  if (git('tag', '-l', tag)) refusals.push(`${tag} exists already.`);
  if (refusals.length) {
    console.error(`Not cutting ${tag}:\n${refusals.map(r => `  - ${r}`).join('\n')}`);
    process.exit(1);
  }

  const dry = flags.has('--dry-run');
  const say = s => console.log((dry ? '[dry-run] ' : '') + s);

  // A ticket that shipped without a plan is claimed now, so the board and the milestone say what went out.
  if (unplanned.length) {
    const b = planByField ? board() : null;
    const mine = unplanned.filter(it => it.repo === REPO);
    say(`claim ${unplanned.length} ticket(s) that closed without a release:${b ? ` Release = ${release}` : ''}${mine.length ? `${b ? ',' : ''} milestone '${ms[0].title}' on the ${mine.length} of this repository` : ''}`);
    if (!dry) for (const it of unplanned) {
      if (b) gh('project', 'item-edit', '--project-id', b.id, '--id', it.id, '--field-id', b.release, '--text', release);
      if (it.repo === REPO) gh('issue', 'edit', String(it.number), '-R', REPO, '--milestone', ms[0].title);
    }
  }
  const themes = ms.map(m => parseMilestone(m.title)?.theme).filter(Boolean);
  const message = `${NAME} ${tag}${themes.length ? ` — ${themes.join(' + ')}` : ''}`;
  say(`git tag -a ${tag} -m "${message}" && git push origin ${tag}`);
  if (!dry) { git('tag', '-a', tag, '-m', message); git('push', 'origin', tag); }
  if (CONFIG.pipeline) say(`${CONFIG.pipeline} publishes on the tag, with ${notes.f} as its body`);
  else {
    // The front matter is for the tools, not for readers: the body is headed by its title instead.
    say(`gh release create ${tag} --title "${releaseName(tag)}" --notes-file ${notes.f} (headed by its title, without its front matter)`);
    if (!dry) {
      const body = join(mkdtempSync(join(tmpdir(), 'release-')), `${tag}.md`);
      writeFileSync(body, compose(notes.text));
      gh('release', 'create', tag, '-R', REPO, '--title', releaseName(tag), '--notes-file', body);
    }
  }
  for (const m of ms) {
    say(`close '${m.title}': Shipped in ${tag}.`);
    if (!dry) gh('api', '-X', 'PATCH', `repos/${REPO}/milestones/${m.number}`, '-f', 'state=closed', '-f', `description=${`${(m.description ?? '').trim()} Shipped in ${tag}.`.trim()}`);
  }
  console.log(`\nNext: ${fill(CONFIG.afterCut, { tag, next: nextMinor(full), current: propsVersion() ?? '' })}`);
  process.exit(0);
}

console.error(USAGE);
process.exit(2);
