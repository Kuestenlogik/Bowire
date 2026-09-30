#!/usr/bin/env node
// Release bookkeeping for a product on the Bowire board (docs/contributing/project-board.md,
// "Products, releases and milestones"). The rules live in release-plan.mjs; this is the part
// that talks to GitHub.
//
// A release is planned, not reconstructed: every ticket names its Product, the Release it is
// planned for (`2.8`) and the milestone of that release (`v2.8 — <theme>`). A milestone with
// nothing open is a release that is due.
//
//   status                      The product's open releases, lowest first, with what is still
//                               open in each, and which one is due.
//   notes <version>             Drafts docs/release-notes/v<version>.md from the plan — the
//                               tickets planned for this release, grouped by area — unless the
//                               file exists (--force rewrites it). Prints what is still open and
//                               what closed since the last tag without a plan, for checking.
//   cut <version> [--dry-run]   Refuses while a planned ticket is open or the notes are still a
//                               draft; claims the tickets that closed since the last tag without
//                               a release; tags with a message naming the milestone and pushes
//                               (the release pipeline publishes on the tag); closes the release's
//                               milestone.
//
// Options: --product <name> (default: the repository's product), --since <tag> (default: the
// latest tag). Needs the gh CLI signed in, with project scope for the board.
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync, existsSync, readFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { planFor, releases, releaseOfVersion, productOfRepo, view, parseMilestone, releaseMatches } from './release-plan.mjs';

const [command, ...rest] = process.argv.slice(2);
const valued = new Set(['--since', '--product']);
const flags = new Set(), args = [], opts = new Map();
for (let i = 0; i < rest.length; i++) {
  if (valued.has(rest[i])) opts.set(rest[i], rest[++i]);
  else if (rest[i].startsWith('--')) flags.add(rest[i]);
  else args.push(rest[i]);
}
const gh = (...a) => execFileSync('gh', a, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'], maxBuffer: 64 * 1024 * 1024 });
const ghJson = (...a) => JSON.parse(gh(...a));
const graphql = (query, vars = {}) => {
  const a = ['api', 'graphql', '-f', `query=${query}`];
  for (const [k, v] of Object.entries(vars)) a.push('-F', `${k}=${v}`);
  return ghJson(...a).data;
};
const git = (...a) => execFileSync('git', a, { encoding: 'utf8' }).trim();

const ORG = 'Kuestenlogik', PROJECT = 2;
const repo = gh('repo', 'view', '--json', 'nameWithOwner', '-q', '.nameWithOwner').trim();
const product = opts.get('--product') ?? productOfRepo(repo);
if (!product) { console.error(`${repo} is not a product on the board — pass --product`); process.exit(2); }

// ── the board ────────────────────────────────────────────────────────────────
function board() {
  const d = graphql(`{ organization(login: "${ORG}") { projectV2(number: ${PROJECT}) { id fields(first: 40) { nodes { ... on ProjectV2FieldCommon { id name } } } } } }`);
  const p = d.organization.projectV2;
  const field = n => p.fields.nodes.find(f => f?.name === n)?.id ?? null;
  return { id: p.id, release: field('Release') };
}
function boardItems() {
  const items = [];
  let cursor = null;
  for (;;) {
    const d = graphql(`query($c: String) { organization(login: "${ORG}") { projectV2(number: ${PROJECT}) { items(first: 100, after: $c) { pageInfo { hasNextPage endCursor } nodes { id
      product: fieldValueByName(name: "Product") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
      release: fieldValueByName(name: "Release") { ... on ProjectV2ItemFieldTextValue { text } }
      area: fieldValueByName(name: "Area") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
      content { __typename ... on Issue { number title state stateReason closedAt url issueType { name } milestone { title } repository { nameWithOwner } } } } } } } }`, cursor ? { c: cursor } : {});
    const page = d.organization.projectV2.items;
    for (const n of page.nodes) if (n.content?.__typename === 'Issue') items.push(view(n));
    if (!page.pageInfo.hasNextPage) return items;
    cursor = page.pageInfo.endCursor;
  }
}
const milestones = () => ghJson('api', `repos/${repo}/milestones?state=all&per_page=100`);
const ref = it => `${it.repo === repo ? '' : it.repo}#${it.number}`;
const lastTag = () => opts.get('--since') ?? (() => { try { return git('describe', '--tags', '--abbrev=0'); } catch { return null; } })();
const tagDate = tag => tag ? git('log', '-1', '--format=%cI', tag) : null;

// ── status ───────────────────────────────────────────────────────────────────
if (command === 'status') {
  const items = boardItems();
  const list = releases(milestones(), repo, product, items);
  if (list.length === 0) { console.log(`No open release of ${product}.`); process.exit(0); }
  for (const r of list) {
    console.log(`${r.milestone.title.padEnd(72)} ${(r.open === 0 ? 'ready' : `${r.open} open`).padStart(8)} · ${r.done} done`);
  }
  const since = lastTag();
  const { unplanned } = planFor(items, product, '0.0.0', tagDate(since));
  console.log('');
  const due = list.find(r => r.open === 0 && r.done > 0);
  if (due) console.log(`Release due: ${product} v${due.version} (${due.milestone.title}). Next: node scripts/ci/release.mjs notes ${due.version}.0`);
  else console.log(`No release due: v${list[0].version} still has ${list[0].open} open ticket(s).`);
  if (unplanned.length) console.log(`\n${unplanned.length} ticket(s) closed since ${since} without a release — the next cut claims them:\n${unplanned.map(it => `  ${ref(it)} ${it.title}`).join('\n')}`);
  process.exit(0);
}

// ── notes / cut ──────────────────────────────────────────────────────────────
if (command === 'notes' || command === 'cut') {
  const [raw] = args;
  if (!raw) { console.error('usage: release.mjs notes|cut <version> [--dry-run] [--force]'); process.exit(2); }
  const version = raw.replace(/^v/, '');
  const tag = `v${version}`;
  const release = releaseOfVersion(version);
  const items = boardItems();
  const since = lastTag();
  const { shipped, stillOpen, unplanned } = planFor(items, product, version, tagDate(since));
  const ms = milestones().filter(m => { const p = parseMilestone(m.title); return p && (p.product ?? productOfRepo(repo)) === product && releaseMatches(p.version, version); });
  const notesPath = `docs/release-notes/${tag}.md`;

  for (const it of stillOpen) console.error(`still open, planned for ${release}: ${ref(it)} ${it.title}`);
  if (unplanned.length) console.error(`closed since ${since} without a release (this cut claims them):\n${unplanned.map(it => `  ${ref(it)} ${it.title}`).join('\n')}`);

  if (command === 'notes') {
    // Grouped by area: the notes are read by feature, not by ticket type.
    const groups = new Map();
    for (const it of [...shipped, ...unplanned]) {
      const key = it.area ?? 'other';
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(it);
    }
    let md = `---\n# The release's \`#\` heading. A sentence about this delivery, not the version.\ntitle: <fill in before the tag>\nversion: ${version}\n---\n\n`;
    md += `<One-sentence frame for what ${product} ${version} is about.>\n\n`;
    md += `<!-- Drafted by scripts/ci/release.mjs from the plan: Product ${product}, Release ${release}${ms.length ? `, milestone ${ms.map(m => m.title).join(' + ')}` : ''}.\n     ${shipped.length} planned ticket(s) done, ${unplanned.length} closed without a plan since ${since ?? 'the start'}.\n     Turn the lists into prose. Keep at least one ### heading: the pipeline counts them. -->\n\n`;
    for (const [area, list] of [...groups.entries()].sort((a, b) => a[0].localeCompare(b[0]))) {
      md += `## ${area}\n\n### <headline>\n\n${list.sort((a, b) => a.number - b.number).map(it => `- ${it.title} ([${ref(it)}](${it.url}))`).join('\n')}\n\n`;
    }
    mkdirSync(dirname(notesPath), { recursive: true });
    // Create exclusively rather than check-then-write: a draft someone is
    // editing is never overwritten by a race, only by an explicit --force.
    try {
      writeFileSync(notesPath, md, { flag: flags.has('--force') ? 'w' : 'wx' });
    } catch (e) {
      if (e.code !== 'EEXIST') throw e;
      console.log(`${notesPath} exists — edit it, or pass --force to draft it again.`);
      process.exit(0);
    }
    console.log(`→ ${notesPath}: ${shipped.length + unplanned.length} ticket(s) in ${groups.size} group(s). Write it, then: node scripts/ci/release.mjs cut ${version}`);
    process.exit(0);
  }

  // cut
  if (product !== productOfRepo(repo)) { console.error(`cut tags this repository; ${product} is released by its own pipeline.`); process.exit(2); }
  if (stillOpen.length) { console.error(`Not cutting: ${stillOpen.length} ticket(s) planned for ${release} are open. Finish them or move them to a later release.`); process.exit(1); }
  // Read, don't check-then-read: the file could go between the two calls.
  let notes;
  try {
    notes = readFileSync(notesPath, 'utf8');
  } catch (e) {
    if (e.code !== 'ENOENT') throw e;
    console.error(`no ${notesPath} — run 'notes ${version}' and write it first`);
    process.exit(2);
  }
  if (/title:\s*<fill in/.test(notes) || /<headline>/.test(notes) || !/^### /m.test(notes)) {
    console.error(`${notesPath} is still a draft: it needs a real title, no <headline> placeholders, and at least one ### section.`);
    process.exit(1);
  }
  const dry = flags.has('--dry-run');
  const say = s => console.log((dry ? '[dry-run] ' : '') + s);

  // A ticket that shipped without a plan is claimed now, so the board says what went out.
  if (unplanned.length) {
    const b = board();
    say(`set Release = ${release} on ${unplanned.length} ticket(s) that closed without one`);
    if (!dry) for (const it of unplanned) gh('project', 'item-edit', '--project-id', b.id, '--id', it.id, '--field-id', b.release, '--text', release);
  }
  const themes = ms.map(m => parseMilestone(m.title)?.theme).filter(Boolean);
  const message = `${repo.split('/')[1]} ${tag}${themes.length ? ` — ${themes.join(' + ')}` : ''}`;
  say(`git tag -a ${tag} -m "${message}" && git push origin ${tag}`);
  if (!dry) { git('tag', '-a', tag, '-m', message); git('push', 'origin', tag); }
  if (existsSync('.github/workflows/release.yml')) say('the release pipeline publishes on the tag, with ' + notesPath + ' as its body');
  else { say(`gh release create ${tag} --notes-file ${notesPath}`); if (!dry) gh('release', 'create', tag, '--repo', repo, '--title', version, '--notes-file', notesPath); }
  for (const m of ms) {
    if (m.open_issues > 0) { say(`keep '${m.title}' open: ${m.open_issues} issue(s) still in it`); continue; }
    say(`close '${m.title}': Ausgeliefert in ${tag}`);
    if (!dry) gh('api', '-X', 'PATCH', `repos/${repo}/milestones/${m.number}`, '-f', 'state=closed', '-f', `description=${(m.description ?? '').trim()} Ausgeliefert in ${tag}.`.trim());
  }
  console.log(`\nNext: set version in docs/release-notes/upcoming.md to the next release, and plan it — every ticket names Product, Release and milestone.`);
  process.exit(0);
}

console.error('usage: release.mjs status | notes <version> [--force] | cut <version> [--dry-run]   (--product <name>, --since <tag>)');
process.exit(2);
