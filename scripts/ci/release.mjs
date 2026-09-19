#!/usr/bin/env node
// Release bookkeeping — one tool, the same in every Küstenlogik repository.
//
// Milestones are ordered work sections `M<n> — <theme>`; a release gets its version number
// only when it is cut and names the sections it ships. A section is delivered in one release or
// several — the last cut finishes it, an `--interim` cut before that ships what is closed so far
// and leaves it open. A sibling repository on the product's board mirrors the sections as its own
// milestones (same titles); the board's Release field, where the board has one, carries the
// delivery a ticket shipped in: the full version, closed tickets only, written once.
//
//   status                              The sections in order with open/closed counts (main repo
//                                       and its mirrors), whether a release is due — the frontmost
//                                       section has no open ticket left — and what blocks it.
//   notes <version> [<section>…]        Drafts the notes for the sections (default: the frontmost
//                                       complete ones): header naming them, the closed tickets of
//                                       the main repo AND of every sibling that mirrors the section,
//                                       grouped by repository, type (Feature / Bug / Task) and area,
//                                       what is not in, and what closed since the last tag outside
//                                       the shipped sections. Writes artifacts/release/<tag>.md.
//                                       --highlights prints only the grouped ticket list.
//   cut <version> [<section>…] [--interim] [--dry-run]
//                                       Refuses while a shipped section (or a mirror) has an open
//                                       ticket — --interim overrides that for an in-between
//                                       delivery; tags with a message naming the sections and
//                                       pushes; publishes the drafted notes as the GitHub release
//                                       unless a release pipeline (.github/workflows/release.yml)
//                                       does that on the tag; closes the sections and their mirrors
//                                       with "Ausgeliefert in <tag>" — but only those with nothing
//                                       left open; stamps the board's Release field on every closed
//                                       item that does not already carry one.
//
// Needs the gh CLI signed in (project scope for the board). Sections are given as M-number ("M1")
// or full title.
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync, readFileSync, existsSync } from 'node:fs';

const [command, ...rest] = process.argv.slice(2);
const valued = new Set(['--since']);
const flags = new Set(), args = [], opts = new Map();
for (let i = 0; i < rest.length; i++) {
  if (valued.has(rest[i])) opts.set(rest[i], rest[++i]);
  else if (rest[i].startsWith('--')) flags.add(rest[i]);
  else args.push(rest[i]);
}
const gh = (...a) => execFileSync('gh', a, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] });
const ghJson = (...a) => JSON.parse(gh(...a));
const graphql = (query, vars = {}) => {
  const a = ['api', 'graphql', '-f', `query=${query}`];
  for (const [k, v] of Object.entries(vars)) a.push('-F', `${k}=${v}`);
  return ghJson(...a).data;
};
const git = (...a) => execFileSync('git', a, { encoding: 'utf8' }).trim();

const repo = gh('repo', 'view', '--json', 'nameWithOwner', '-q', '.nameWithOwner').trim();
const [owner, name] = repo.split('/');
const order = t => { const m = /^M(\d+)/.exec(t ?? ''); return m ? Number(m[1]) : null; };
const themeOf = t => t.replace(/^(?:M\d+|v[\w.-]+)\s*(?:[—-]\s*)?/, '').trim();

// ── the board, when the repo has one ─────────────────────────────────────────
function board() {
  const d = graphql(`{ repository(owner: "${owner}", name: "${name}") { projectsV2(first: 5) { nodes { id number title fields(first: 30) { nodes { ... on ProjectV2SingleSelectField { id name options { id name } } } } } } } }`);
  const p = d.repository.projectsV2.nodes[0];
  if (!p) return null;
  const field = n => p.fields.nodes.find(f => f?.name === n) ?? null;
  return { id: p.id, number: p.number, title: p.title, release: field('Release'), area: field('Area') };
}
function boardItems(b) {
  const items = [];
  let cursor = null;
  while (true) {
    const d = graphql(`query($c: String) { node(id: "${b.id}") { ... on ProjectV2 { items(first: 100, after: $c) { pageInfo { hasNextPage endCursor } nodes { id
      release: fieldValueByName(name: "Release") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
      area: fieldValueByName(name: "Area") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
      content { __typename ... on Issue { number title state closedAt url issueType { name } milestone { title } repository { nameWithOwner } labels(first: 10) { nodes { name } } } } } } } } }`, cursor ? { c: cursor } : {});
    const page = d.node.items;
    for (const n of page.nodes) if (n.content?.__typename === 'Issue') items.push(n);
    if (!page.pageInfo.hasNextPage) break;
    cursor = page.pageInfo.endCursor;
  }
  return items;
}

// ── sections ─────────────────────────────────────────────────────────────────
function sections() {
  return ghJson('api', `repos/${repo}/milestones?state=all&per_page=100`)
    .filter(m => order(m.title) !== null)
    .sort((a, b) => order(a.title) - order(b.title));
}
function resolve(names, all) {
  return names.map(n => all.find(m => m.title === n || m.title.startsWith(n + ' '))
    ?? (() => { throw new Error(`no section '${n}' — have: ${all.map(m => m.title).join(', ')}`); })());
}
/** The mirrors of a section in the sibling repos on the board: same title. */
function mirrors(title, b) {
  if (!b) return [];
  const repos = new Set(boardItems(b).map(i => i.content.repository.nameWithOwner).filter(r => r !== repo));
  const out = [];
  for (const r of repos) {
    const m = ghJson('api', `repos/${r}/milestones?state=all&per_page=100`).find(x => x.title === title);
    if (m) out.push({ repo: r, ...m });
  }
  return out;
}

// ── status ───────────────────────────────────────────────────────────────────
if (command === 'status') {
  const b = board();
  const open = sections().filter(m => m.state === 'open');
  let due = null;
  for (const m of open) {
    const mir = mirrors(m.title, b);
    const openTotal = m.open_issues + mir.reduce((s, x) => s + x.open_issues, 0);
    const closedTotal = m.closed_issues + mir.reduce((s, x) => s + x.closed_issues, 0);
    console.log(`${m.title.padEnd(56)} ${(openTotal === 0 ? 'complete' : `${openTotal} open`).padStart(10)} · ${closedTotal} closed${mir.length ? ` · mirrored in ${mir.length} repo(s)` : ''}`);
    if (!due && openTotal === 0) due = m;
  }
  console.log('');
  if (!due) {
    const first = open[0];
    if (!first) { console.log('No open section.'); process.exit(0); }
    console.log(`No release due: ${first.title} still has open ticket(s):`);
    for (const i of ghJson('issue', 'list', '--repo', repo, '--state', 'open', '--milestone', first.title, '--limit', '200', '--json', 'number,title')) console.log(`  #${i.number} ${i.title}`);
    for (const mir of mirrors(first.title, b)) for (const i of ghJson('issue', 'list', '--repo', mir.repo, '--state', 'open', '--milestone', mir.title, '--limit', '200', '--json', 'number,title')) console.log(`  ${mir.repo}#${i.number} ${i.title}`);
  } else {
    const prs = ghJson('pr', 'list', '--repo', repo, '--state', 'open', '--json', 'number');
    console.log(`Release due: ${due.title} is complete.${prs.length ? ` ${prs.length} open pull request(s) first.` : ''}`);
    console.log(`Next: node ${process.argv[1].replace(/\\/g, '/').replace(/^.*\/(scripts\/)/, '$1')} notes <version> ${due.title.split(' ')[0]}`);
  }
  process.exit(0);
}

// ── notes / cut ──────────────────────────────────────────────────────────────
if (command === 'notes' || command === 'cut') {
  const [version, ...names] = args;
  if (!version) { console.error('usage: release.mjs notes|cut <version> [<section>…] [--interim]'); process.exit(2); }
  const all = sections();
  const b = board();
  let chosen = names.length ? resolve(names, all) : [];
  if (chosen.length === 0) {
    // Default: the frontmost complete section, and the next ones while they are complete too.
    for (const m of all.filter(m => m.state === 'open')) {
      const openTotal = m.open_issues + mirrors(m.title, b).reduce((s, x) => s + x.open_issues, 0);
      if (openTotal === 0) chosen.push(m); else break;
    }
    if (chosen.length === 0) { console.error('No complete section to ship — name one explicitly.'); process.exit(1); }
  }
  const titles = chosen.map(m => m.title);
  const tag = version.startsWith('v') ? version : 'v' + version;
  // The stamp names the delivery: release notes are written per cut, so v2.8.0 and v2.8.1
  // have to stay apart. A prerelease stamps the version it previews.
  const shippedVersion = 'v' + tag.slice(1).replace(/-.*$/, '');
  const since = opts.get('--since') ?? (() => { try { return git('describe', '--tags', '--abbrev=0'); } catch { return null; } })();
  const notesPath = `artifacts/release/${tag}.md`;

  // What ships: closed issues of the sections in this repo, and of their mirrors via the board.
  const shipped = [];
  const stillOpen = [];
  for (const m of chosen) {
    // GraphQL rather than `gh issue list`: the issue type is not reachable from the latter.
    let cursor = null;
    while (true) {
      const d = graphql(`query($c: String) { repository(owner: "${owner}", name: "${name}") { milestone(number: ${m.number}) { issues(first: 100, after: $c, states: [OPEN, CLOSED]) { pageInfo { hasNextPage endCursor } nodes { number title state url issueType { name } labels(first: 10) { nodes { name } } } } } } }`, cursor ? { c: cursor } : {});
      const page = d.repository.milestone.issues;
      for (const i of page.nodes) (i.state === 'CLOSED' ? shipped : stillOpen).push({ repo, number: i.number, title: i.title, url: i.url, labels: i.labels.nodes.map(l => l.name), type: i.issueType?.name ?? null, area: null });
      if (!page.pageInfo.hasNextPage) break;
      cursor = page.pageInfo.endCursor;
    }
  }
  if (b) {
    for (const it of boardItems(b)) {
      const c = it.content;
      if (!titles.includes(c.milestone?.title ?? '') || c.repository.nameWithOwner === repo) continue;
      (c.state === 'CLOSED' ? shipped : stillOpen).push({ repo: c.repository.nameWithOwner, number: c.number, title: c.title, url: c.url, labels: c.labels.nodes.map(l => l.name), type: c.issueType?.name ?? null, area: it.area?.name ?? null });
    }
    // Types and areas for the main repo's issues come from the board too, where they are.
    const byNumber = new Map(boardItems(b).filter(it => it.content.repository.nameWithOwner === repo).map(it => [it.content.number, it]));
    for (const s of shipped) if (s.repo === repo && byNumber.has(s.number)) { const it = byNumber.get(s.number); s.type ??= it.content.issueType?.name ?? null; s.area = it.area?.name ?? null; }
  }
  const areaOf = s => s.area ?? s.labels.filter(l => l.startsWith('area:')).map(l => l.slice(5)).sort().join(' · ') ?? '';
  const typeOf = s => s.type ?? (/^Epic\b/i.test(s.title) ? 'Feature' : 'Feature');
  const grouped = new Map();
  for (const s of shipped) {
    const key = `${s.repo === repo ? '' : s.repo.split('/')[1] + ' · '}${typeOf(s)}`;
    if (!grouped.has(key)) grouped.set(key, []);
    grouped.get(key).push(`#${s.number} ${s.title}${areaOf(s) ? ` _(${areaOf(s)})_` : ''}${s.repo === repo ? '' : ` — ${s.url}`}`);
  }
  const typeRank = k => /Feature$/.test(k) ? 0 : /Bug$/.test(k) ? 1 : 2;
  const keys = [...grouped.keys()].sort((a, c) => (a.includes(' · ') ? 1 : 0) - (c.includes(' · ') ? 1 : 0) || a.localeCompare(c) || typeRank(a) - typeRank(c));

  if (flags.has('--highlights')) {
    for (const k of keys) { console.log(`### ${k}`); console.log(''); for (const l of grouped.get(k)) console.log(`- ${l}`); console.log(''); }
    process.exit(0);
  }

  if (command === 'notes') {
    for (const s of stillOpen) console.error(`WARNING: still open: ${s.repo}#${s.number} ${s.title}`);
    let md = `${name} ${tag} — enthält ${titles.join(', ')}.${since ? ` ${git('rev-list', '--count', `${since}..HEAD`)} Commits seit ${since}.` : ''}\n\n## Themen\n\n`;
    for (const k of keys) md += `### ${k}\n\n${grouped.get(k).map(l => `- ${l}`).join('\n')}\n\n`;
    // What is still open in a shipped section belongs in the notes of an in-between delivery:
    // the header says "enthält <section>", and without this the reader would take that literally.
    if (stillOpen.length) md += `## Noch offen in ${titles.join(', ')}\n\n${stillOpen.map(s => `- ${s.repo === repo ? '' : s.repo}#${s.number} ${s.title}`).join('\n')}\n\n`;
    const others = all.filter(m => m.state === 'open' && !chosen.includes(m));
    if (others.length) md += `## Nicht drin\n\n${others.map(m => `- ${m.title}: ${m.open_issues} offen`).join('\n')}\n\n`;
    if (since) {
      const sinceDate = git('log', '-1', '--format=%cI', since).slice(0, 10);
      const stray = ghJson('issue', 'list', '--repo', repo, '--state', 'closed', '--limit', '500', '--search', `closed:>${sinceDate}`, '--json', 'number,title,milestone')
        .filter(i => !titles.includes(i.milestone?.title ?? ''));
      if (stray.length) md += `## Seit ${since} geschlossen, aber nicht in diesen Abschnitten (prüfen)\n\n${stray.map(i => `- #${i.number} ${i.title}${i.milestone ? ` (${i.milestone.title})` : ' (ohne Meilenstein)'}`).join('\n')}\n`;
    }
    mkdirSync('artifacts/release', { recursive: true });
    writeFileSync(notesPath, md);
    console.log(md);
    console.log(`→ ${notesPath}  (edit, then: node ${process.argv[1].replace(/\\/g, '/').replace(/^.*\/(scripts\/)/, '$1')} cut ${version} ${names.join(' ')})`);
    process.exit(0);
  }

  // cut
  //
  // A section is delivered in one release or several. The default cut is the one that finishes a
  // section, and an open ticket in it means the cut is premature — so it refuses. An in-between
  // delivery is the other case and has to say so: `--interim` ships what is closed, lists the rest
  // as not included, and leaves the milestone open for the cut that does finish it.
  const interim = flags.has('--interim');
  if (stillOpen.length && !interim) {
    for (const s of stillOpen) console.error(`${s.repo}#${s.number} is still open`);
    console.error('Not cutting. Pass --interim to deliver what is closed and keep the section open.');
    process.exit(1);
  }
  if (stillOpen.length) console.log(`[interim] ${stillOpen.length} ticket(s) stay open for a later cut of ${titles.join(', ')}`);
  if (!existsSync(notesPath)) { console.error(`no ${notesPath} — run 'notes' first`); process.exit(2); }
  const dry = flags.has('--dry-run');
  const say = s => console.log((dry ? '[dry-run] ' : '') + s);
  const message = `${name} ${tag} — ${titles.join(', ')}`;
  say(`git tag -a ${tag} -m "${message}" && git push origin ${tag}`);
  if (!dry) { git('tag', '-a', tag, '-m', message); git('push', 'origin', tag); }
  if (existsSync('.github/workflows/release.yml')) {
    say('release pipeline (.github/workflows/release.yml) publishes on the tag; the drafted notes are its editorial body');
  } else {
    say(`gh release create ${tag} --notes-file ${notesPath}`);
    if (!dry) gh('release', 'create', tag, '--repo', repo, '--title', `${name} ${tag}`, '--notes-file', notesPath);
  }
  // Only the cut that finishes a section closes it. A section may be delivered from more than
  // once, and an in-between cut that closed the milestone would drop the section off the roadmap
  // with its remaining tickets still open.
  for (const m of chosen) {
    const targets = [{ repo, ...m }, ...mirrors(m.title, b)];
    const openTotal = targets.reduce((sum, t) => sum + t.open_issues, 0);
    if (openTotal > 0) {
      say(`keep milestone '${m.title}' open: ${openTotal} ticket(s) left — ${tag} delivers from it, it does not finish it`);
      continue;
    }
    for (const target of targets) {
      say(`close ${target.repo} milestone '${target.title}': Ausgeliefert in ${tag}`);
      if (!dry) gh('api', '-X', 'PATCH', `repos/${target.repo}/milestones/${target.number}`, '-f', 'state=closed', '-f', `description=${(target.description ?? '').trim()} Ausgeliefert in ${tag}.`);
    }
  }
  if (b?.release) {
    let option = b.release.options.find(o => o.name === shippedVersion);
    say(`stamp Release = ${shippedVersion} on every closed board item of ${titles.join(', ')}`);
    if (!dry) {
      if (!option) {
        const inner = [{ name: shippedVersion }, ...b.release.options].map(o => `{${o.id ? `id: "${o.id}", ` : ''}name: ${JSON.stringify(o.name)}, color: GRAY, description: ${JSON.stringify(o.id ? '' : `Ausgeliefert als ${tag}`)}}`).join(',');
        graphql(`mutation { updateProjectV2Field(input: {fieldId: "${b.release.id}", name: "Release", singleSelectOptions: [${inner}]}) { projectV2Field { ... on ProjectV2SingleSelectField { id } } } }`);
        option = board().release.options.find(o => o.name === shippedVersion);
      }
      // Closed only: an open ticket has not shipped. First stamp wins: a ticket ships once, and
      // overwriting would drag every earlier ticket of the section onto the newest cut — which
      // would leave the field saying no more than the milestone already says.
      let n = 0, held = 0, earlier = 0;
      for (const it of boardItems(b)) {
        if (!titles.includes(it.content.milestone?.title ?? '')) continue;
        if (it.content.state !== 'CLOSED') { held++; continue; }
        if (it.release?.name) { if (it.release.name !== shippedVersion) earlier++; continue; }
        gh('project', 'item-edit', '--project-id', b.id, '--id', it.id, '--field-id', b.release.id, '--single-select-option-id', option.id); n++;
      }
      console.log(`stamped ${n} item(s)` + (held ? `, ${held} still open` : '') + (earlier ? `, ${earlier} shipped earlier` : ''));
    }
  }
  const bump = existsSync('Directory.Build.props') ? readFileSync('Directory.Build.props', 'utf8').match(/<Version>([^<]+)<\/Version>/)?.[1] : null;
  console.log(bump ? `\nmain still says ${bump}: bump <Version> in Directory.Build.props to the next preview and commit.` : '\nBump the version on main to the next preview and commit.');
  process.exit(0);
}

console.error('usage: release.mjs status | notes <version> [<section>…] [--highlights] | cut <version> [<section>…] [--interim] [--dry-run]');
process.exit(2);
