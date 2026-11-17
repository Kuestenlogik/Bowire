// The release plan: the rules every release script shares (docs/contributing/project-board.md).
//
// This file is the same in every Kuestenlogik repository that carries the release tool
// (scripts/ci/release*.mjs, resolve-milestone.mjs, check-release-plan.mjs and release-tool.test.mjs);
// what differs between them is in scripts/ci/release.config.json beside it. Change the shared files in
// every repository at once, never in one alone.
//
// Three things say where a ticket goes, and they agree:
//
//   Product    the artifact that ships it, and so the version line it counts in. A repository ships
//              its product (`products` in the configuration); a board with a Product field may name
//              another, and a second product of one repository prefixes its milestones
//              (`VS Code v1.1 — <theme>`, `milestonePrefixes`).
//   Release    the version it is planned for, in that product's count: `0.2`, or `0.2.1` for a patch.
//              A text field on the board, set when the ticket is planned, not when it ships. A board
//              without the field plans by the milestone alone, and the milestone's version is the
//              ticket's Release.
//   Milestone  that release: `v0.2 — <theme>`. A milestone with nothing open is a release that is due,
//              once every release before it has shipped.
//
// Everything here is pure apart from reading the configuration: board items in, decisions out. The
// scripts that talk to GitHub are thin around it, so the rules are pinned by tests without a board.
import { readFileSync } from 'node:fs';
import { frontMatterTitle, stripFrontMatter } from './release-body.mjs';

/** The fields a ticket may be required to carry beyond its plan. */
export const REQUIRED_FIELDS = ['Area', 'Type', 'Status'];

/** The values `version.atCut` may take: the props must carry the release, or may carry anything. */
export const VERSION_AT_CUT = ['equal', 'free'];

/**
 * What is wrong with a configuration; nothing when it is complete. Every key is required, so that no
 * value of one repository hides in the code as a default.
 */
export function validateConfig(c) {
  const out = [];
  const isText = v => typeof v === 'string' && v.trim() !== '';
  const isTextOrNull = v => v === null || isText(v);
  const isTextList = v => Array.isArray(v) && v.every(isText);
  if (!c || typeof c !== 'object') return ['the configuration is no object'];
  if (!/^[^/\s]+\/[^/\s]+$/.test(c.repository ?? '')) out.push('repository: owner/name');
  const b = c.board;
  if (!b || typeof b !== 'object') out.push('board: an object');
  else {
    if (!isText(b.owner)) out.push('board.owner: the organisation of the project');
    if (!Number.isInteger(b.project) || b.project < 1) out.push('board.project: the project number');
    for (const k of ['release', 'product', 'area', 'status']) {
      if (!(k in b) || !isTextOrNull(b[k])) out.push(`board.${k}: the field's name, or null where the board has none`);
    }
  }
  if (!c.products || typeof c.products !== 'object' || Object.values(c.products).some(v => !isText(v))) out.push('products: repository name → product');
  else if (!c.products[String(c.repository ?? '').split('/').pop()]) out.push('products: names no product of the repository itself');
  if (!isTextList(c.milestonePrefixes)) out.push('milestonePrefixes: a list of product names');
  if (!Array.isArray(c.required) || c.required.some(f => !REQUIRED_FIELDS.includes(f))) out.push(`required: a list of ${REQUIRED_FIELDS.join(', ')}`);
  else {
    if (c.required.includes('Area') && !b?.area) out.push('required names Area, but board.area is null');
    if (c.required.includes('Status') && !b?.status) out.push('required names Status, but board.status is null');
  }
  if (!isTextList(c.ignore)) out.push('ignore: a list of markers');
  const v = c.version;
  if (v !== null && (!v || typeof v !== 'object' || !isText(v.file) || !isTextList(v.elements) || v.elements.length === 0 || !VERSION_AT_CUT.includes(v.atCut))) {
    out.push(`version: null (the version comes from the tag), or { file, elements, atCut: ${VERSION_AT_CUT.join(' | ')} }`);
  }
  const n = c.notes;
  if (!n || typeof n !== 'object' || !isTextList(n.also) || !isText(n.upgrading) || !n.upgrading.includes('{file}')) {
    out.push('notes: { also: further accepted notes files, upgrading: the Upgrading sentence naming {file} }');
  }
  if (!('pipeline' in c) || !isTextOrNull(c.pipeline)) out.push('pipeline: the workflow that publishes on the tag, or null');
  if (!isText(c.afterCut)) out.push('afterCut: the next step after a cut');
  return out;
}

/** Reads and checks a configuration; a configuration that is not complete is a programming error. */
export function loadConfig(url) {
  const config = JSON.parse(readFileSync(url, 'utf8'));
  const wrong = validateConfig(config);
  if (wrong.length) throw new Error(`${url}: ${wrong.join('; ')}`);
  return config;
}

// ── rules that need no configuration ─────────────────────────────────────────

/** Whether a Release value has the planned form: `0.2` or `0.2.1`. */
export function isRelease(text) {
  return /^\d+\.\d+(?:\.\d+)?$/.test(String(text ?? ''));
}

/**
 * `0.2.0` → `0.2` (a minor release is planned as major.minor); `0.2.1` → `0.2.1`; a leading `v` and a
 * prerelease suffix are dropped, so `0.2.0-preview.1` belongs to release `0.2`.
 */
export function releaseOfVersion(version) {
  const v = String(version ?? '').replace(/^v/, '').replace(/-.*$/, '');
  const [major, minor = '0', patch = '0'] = v.split('.');
  return patch === '0' ? `${major}.${minor}` : `${major}.${minor}.${patch}`;
}

/** Whether a ticket planned for `release` ships in `version`: `0.2` in 0.2.0, `0.2.1` only in 0.2.1. */
export function releaseMatches(release, version) {
  if (!release) return false;
  return releaseOfVersion(release) === releaseOfVersion(version);
}

/** Numeric order of versions (`0.10` after `0.9`, `0.2.1` after `0.2`). */
export function compareVersions(a, b) {
  const pa = releaseOfVersion(a).split('.').map(Number);
  const pb = releaseOfVersion(b).split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d !== 0) return d;
  }
  return 0;
}

/** `0.2` or `0.2.0` → `0.2.0`; `0.2.1` stays; null for anything that is no version. */
export function fullVersion(raw) {
  const m = /^v?(\d+)\.(\d+)(?:\.(\d+))?$/.exec(String(raw ?? '').trim());
  return m ? `${m[1]}.${m[2]}.${m[3] ?? '0'}` : null;
}

/** The minor release after `version`: `0.2.0` → `0.3.0`. */
export const nextMinor = version => String(version).replace(/^v?(\d+)\.(\d+)\..*$/, (_, a, b) => `${a}.${Number(b) + 1}.0`);

/**
 * A triage milestone, `Triage — <theme>`: one per repository, without a version. It holds the tickets
 * that arrive by themselves until someone decides whether they become work at all. A ticket in it
 * names its Product but no Release yet; triage ends by closing it or by moving it into the milestone
 * of a release.
 */
export function isTriageMilestone(title) {
  return /^Triage(?:\s+[—-]\s+.+)?$/.test(String(title ?? '').trim());
}

/**
 * Whether release notes are still a draft: the marker `release.mjs notes` writes, a placeholder in
 * angle brackets, a front-matter title that is missing or says nothing, or no `###` section, which the
 * release pipelines count. A placeholder inside another HTML comment is left alone: a curation hint
 * (`Pattern: ### <headline> (#issue)`) is never published.
 */
export function notesAreDraft(text) {
  const s = String(text ?? '');
  if (/<!--\s*draft\b/i.test(s)) return true;
  if (/<(?:headline|one paragraph|one-sentence|fill in)\b[^>]*>/i.test(s.replace(/<!--[\s\S]*?-->/g, ''))) return true;
  if (!frontMatterTitle(s)) return true;
  return !/^### /m.test(stripFrontMatter(s));
}

/** Whether notes carry at least one `###` section, which is how a release pipeline knows curated notes. */
export const hasSections = text => /^### /m.test(stripFrontMatter(text));

/**
 * The release before `tag`, so that "closed since" has a date: the latest release tag (`vX.Y.Z`, no
 * prerelease) dated before `tag`, or before now when `tag` is not cut yet. `tags` are { tag, at }, from
 * git or from the published releases.
 */
export function previousRelease(tag, tags) {
  const mine = tags.find(t => t.tag === tag)?.at ?? new Date().toISOString();
  return tags
    .filter(t => t.tag !== tag && /^v\d+\.\d+\.\d+$/.test(t.tag) && new Date(t.at) < new Date(mine))
    .sort((a, b) => new Date(b.at) - new Date(a.at))[0] ?? null;
}

/** A version element in a props file (`<Version>0.2.0-preview.1</Version>` by default), or null. */
export function versionOfProps(text, element = 'Version') {
  return new RegExp(`<${element}>([^<]+)</${element}>`).exec(String(text ?? ''))?.[1]?.trim() ?? null;
}

/**
 * Why HEAD is not the commit to tag; nothing when it is. `head` and `main` are commit ids, `main` that
 * of origin/main read after a fetch: the tag names the commit main has on GitHub, not a local one
 * nobody else has or one that misses what main gained since. `pullRequests` are the open ones: work
 * waiting outside the release is merged or closed first.
 */
export function headRefusals(head, main, pullRequests = []) {
  const out = [];
  const short = id => String(id ?? '').slice(0, 7);
  if (head !== main) out.push(`HEAD is ${short(head)}, origin/main is ${short(main)}: cut from main as GitHub has it.`);
  if (pullRequests.length) out.push(`${pullRequests.length} pull request(s) open (${pullRequests.map(p => `#${p.number}`).join(', ')}): merge or close them first.`);
  return out;
}

/** Where the notes of a release are drafted: `docs/release-notes/v<version>.md`. */
export const notesPath = tag => `docs/release-notes/${tag}.md`;

/** Where the upgrade notes from `previousTag` live, beside the release notes. */
export const upgradeNotesPath = previousTag => `docs/release-notes/upgrading-from-${previousTag}.md`;

/** Fills `{name}` placeholders of a configured sentence. */
export const fill = (template, values) => String(template).replace(/\{(\w+)\}/g, (all, k) => (k in values ? String(values[k]) : all));

// ── rules bound to a repository's configuration ──────────────────────────────

/** The rules for one repository; `config` as release.config.json holds it. */
export function createPlan(config) {
  const REPO = config.repository;
  const NAME = REPO.split('/')[1];
  const PRODUCT_OF_REPO = { ...config.products };
  const planByField = config.board.release !== null;

  /** The default product of `owner/name` or `name`; null for a repository outside the plan. */
  const productOfRepo = repo => PRODUCT_OF_REPO[String(repo ?? '').split('/').pop()] ?? null;

  /**
   * `v0.2 — Theme` → { product: null, version: '0.2', theme: 'Theme' }; `VS Code v1.1 — Theme` →
   * { product: 'VS Code', … } where `VS Code` is a configured prefix; anything else → null. A null
   * product means the repository's own. A prefix that is no product of the repository (`attempt-1 · v0.3`)
   * and a title without a version (`M2 — …`, `Triage — …`) are no release.
   */
  function parseMilestone(title) {
    const m = /^(?:(.+?)\s+)?v(\d+\.\d+(?:\.\d+)?)(?:\s+[—-]\s+(.*))?$/.exec(String(title ?? '').trim());
    if (!m) return null;
    if (m[1] && !config.milestonePrefixes.includes(m[1])) return null;
    return { product: m[1] ?? null, version: m[2], theme: (m[3] ?? '').trim() || null };
  }

  /** Whether an issue body carries a marker of a tracking issue, which is no work any release ships. */
  const isIgnored = body => typeof body === 'string' && config.ignore.some(marker => body.includes(marker));

  /** The GraphQL selection of one board item that `view` reads, so every script asks the same question. */
  const field = (alias, name, type, value) => (name ? `\n  ${alias}: fieldValueByName(name: ${JSON.stringify(name)}) { ... on ${type} { ${value} } }` : '');
  const select = (alias, name) => field(alias, name, 'ProjectV2ItemFieldSingleSelectValue', 'name');
  const ISSUE_FIELDS = '__typename number title url state stateReason closedAt body issueType { name } milestone { title state } repository { nameWithOwner } labels(first: 20) { nodes { name } }';
  const ITEM_FIELDS = `id${field('release', config.board.release, 'ProjectV2ItemFieldTextValue', 'text')}${select('product', config.board.product)}${select('area', config.board.area)}${select('status', config.board.status)}
  content { ... on Issue { ${ISSUE_FIELDS} } }`;

  /**
   * A board item reduced to what the rules read; `raw` as `ITEM_FIELDS` selects it, or `{ content }` for an
   * issue that is not on the board. The Area is the board's, else the issue's `area:` label, which the boards
   * keep in step with the field.
   */
  function view(raw) {
    const c = raw?.content ?? {};
    const repo = c.repository?.nameWithOwner ?? '';
    const milestone = c.milestone?.title ?? null;
    const product = raw?.product?.name ?? productOfRepo(repo);
    const fromMilestone = parseMilestone(milestone);
    const release = planByField
      ? (raw?.release?.text?.trim() || null)
      : (fromMilestone && (fromMilestone.product ?? productOfRepo(repo)) === product ? fromMilestone.version : null);
    return {
      id: raw?.id,
      repo,
      number: c.number,
      title: c.title,
      url: c.url,
      state: c.state,
      notPlanned: c.stateReason === 'NOT_PLANNED',
      closedAt: c.closedAt ?? null,
      milestone,
      milestoneClosed: c.milestone?.state === 'CLOSED',
      product,
      release,
      area: raw?.area?.name ?? (c.labels?.nodes ?? []).map(l => l.name).find(n => n.startsWith('area:'))?.slice(5) ?? null,
      status: raw?.status?.name ?? null,
      type: c.issueType?.name ?? null,
      ignored: isIgnored(c.body),
    };
  }

  /**
   * What a release of `product` at `version` contains: the tickets planned for it that are done, those
   * still open, and, since `since` (the date of the previous release), the tickets that closed without
   * being planned for any release, which the cut then claims. A ticket closed as not planned never ships.
   */
  function planFor(items, product, version, since = null) {
    const after = since ? new Date(since).getTime() : null;
    const shipped = [], stillOpen = [], unplanned = [];
    for (const it of items) {
      if (it.product !== product || it.ignored) continue;
      if (releaseMatches(it.release, version)) {
        if (it.state === 'OPEN') stillOpen.push(it);
        else if (!it.notPlanned) shipped.push(it);
        continue;
      }
      if (!it.release && it.state === 'CLOSED' && !it.notPlanned && after !== null
          && it.closedAt && new Date(it.closedAt).getTime() > after) {
        unplanned.push(it);
      }
    }
    return { shipped, stillOpen, unplanned };
  }

  /**
   * Why an open ticket is not planned; nothing when it is. A planned ticket names its product, the
   * fields the configuration requires, the Release it is planned for (on a board with the field) and
   * the open milestone of that release, and Release and milestone agree; or it sits in its
   * repository's triage milestone, not planned yet.
   */
  function problems(it) {
    const out = [];
    if (it.state !== 'OPEN' || it.ignored) return out;
    if (!it.product) out.push('no Product');
    for (const f of config.required) if (!it[f.toLowerCase()]) out.push(`no ${f}`);
    if (config.required.includes('Status') && it.status === 'Done') out.push('open, but Done on the board');
    if (isTriageMilestone(it.milestone)) {
      if (planByField && it.release) out.push(`milestone is ${it.milestone}, Release is ${it.release}: move it to the milestone of that release`);
      return out;
    }
    if (planByField) {
      if (!it.release) out.push('no Release');
      else if (!isRelease(it.release)) out.push(`Release '${it.release}' is not a version such as 0.2 or 0.2.1`);
    }
    if (!it.milestone) { out.push('no milestone'); return out; }
    const ms = parseMilestone(it.milestone);
    if (!ms) { out.push(`milestone '${it.milestone}' names no release`); return out; }
    const msProduct = ms.product ?? productOfRepo(it.repo);
    if (it.product && msProduct !== it.product) out.push(`milestone is ${msProduct}, Product is ${it.product}`);
    if (it.milestoneClosed) out.push(`milestone v${ms.version} is closed: that release has shipped`);
    if (planByField && it.release && isRelease(it.release) && releaseOfVersion(ms.version) !== releaseOfVersion(it.release)) {
      out.push(`milestone is v${ms.version}, Release is ${it.release}`);
    }
    return out;
  }

  /**
   * The open releases of `product`, lowest version first, each with how much of it is still open: on
   * the board (`open`, `done`) and in its milestone (`milestoneOpen`, GitHub's own count, which also
   * sees an issue that never reached the board).
   */
  function releases(milestones, items, product = productOfRepo(REPO)) {
    return milestones
      .filter(m => m.state === 'open')
      .map(m => ({ m, parsed: parseMilestone(m.title) }))
      .filter(x => x.parsed && (x.parsed.product ?? productOfRepo(REPO)) === product)
      .map(({ m, parsed }) => {
        const planned = items.filter(it => it.product === product && !it.ignored && releaseMatches(it.release, parsed.version));
        return {
          milestone: m,
          version: parsed.version,
          theme: parsed.theme,
          open: planned.filter(it => it.state === 'OPEN').length,
          done: planned.filter(it => it.state === 'CLOSED' && !it.notPlanned).length,
          milestoneOpen: m.open_issues ?? 0,
        };
      })
      .sort((a, b) => compareVersions(a.version, b.version));
  }

  /** The open milestones of the repository that name no release and are no triage: to be renamed. */
  const unversioned = milestones => milestones.filter(m => m.state === 'open' && !parseMilestone(m.title) && !isTriageMilestone(m.title));

  /** The milestones of `product` that `version` ships. */
  const milestonesOf = (milestones, product, version) =>
    milestones.filter(m => { const p = parseMilestone(m.title); return p && (p.product ?? productOfRepo(REPO)) === product && releaseMatches(p.version, version); });

  /** The section "Upgrading" of drafted notes, where upgrade notes from `previousTag` are written. */
  function upgradeSection(previousTag, exists) {
    if (!previousTag || !exists) return '';
    const file = upgradeNotesPath(previousTag).split('/').pop();
    return `## Upgrading\n\n${fill(config.notes.upgrading, { previous: previousTag, file })}\n\n`;
  }

  /** The tickets of a draft grouped by area, areas by name and tickets by number. */
  function groups(tickets) {
    const byArea = new Map();
    for (const it of tickets) {
      const key = it.area ?? 'other';
      if (!byArea.has(key)) byArea.set(key, []);
      byArea.get(key).push(it);
    }
    return [...byArea.entries()].sort((a, b) => a[0].localeCompare(b[0])).map(([area, list]) => [area, [...list].sort((a, b) => a.number - b.number)]);
  }

  const ref = it => `${it.repo === REPO ? '' : it.repo}#${it.number}`;
  const line = it => `- ${it.title} ([${ref(it)}](${it.url}))${it.type && it.type !== 'Feature' ? ` _(${it.type})_` : ''}`;

  /** The grouped ticket list alone, as `notes --highlights` prints it for a pipeline to fill in. */
  const highlights = tickets => groups(tickets).map(([area, list]) => `### ${area}\n\n${list.map(line).join('\n')}\n`).join('\n');

  /**
   * The drafted notes of a release: front matter with the title still to write, the draft marker, the
   * frame, the Upgrading section, and the tickets by area under a headline still to write. `cut` refuses
   * them until every placeholder and the marker are gone.
   */
  function draftNotes({ product, version, release, milestones, shipped, unplanned, since, commits, upgrading }) {
    const named = config.board.product ? `Product ${product}, ` : '';
    let md = `---\n# The release's \`#\` heading: a sentence about this delivery, not the version.\ntitle: <fill in before the tag>\nversion: ${version}\n---\n\n`;
    md += `<!-- draft: written by scripts/ci/release.mjs notes from the plan: ${named}Release ${release}${milestones.length ? `, milestone ${milestones.map(m => m.title).join(' + ')}` : ', no milestone'}.\n`;
    md += `     ${shipped.length} planned ticket(s) done, ${unplanned.length} closed without a plan since ${since ?? 'the start'}.${commits === null || commits === undefined ? '' : ` ${commits} commit(s) since ${since}.`}\n`;
    md += `     Turn the lists into prose with a headline over each, then delete this comment: cut refuses\n     the notes while it or a placeholder in angle brackets is left. -->\n\n`;
    md += `<One paragraph: what ${product} ${version} is about.>\n\n`;
    md += upgradeSection(since, upgrading);
    for (const [area, list] of groups([...shipped, ...unplanned])) md += `## ${area}\n\n### <headline>\n\n${list.map(line).join('\n')}\n\n`;
    return md;
  }

  /** The release that is due, or null: only the lowest open release, with nothing open and something done. */
  const complete = r => r.open === 0 && r.milestoneOpen === 0;
  const dueRelease = list => (list[0] && complete(list[0]) && list[0].done > 0 ? list[0] : null);
  const releasesBefore = (list, version) => list.filter(r => compareVersions(r.version, version) < 0);

  return {
    CONFIG: config, REPO, NAME, PRODUCT_OF_REPO, ISSUE_FIELDS, ITEM_FIELDS, planByField,
    productOfRepo, parseMilestone, isIgnored, view, planFor, problems, releases, unversioned,
    milestonesOf, upgradeSection, groups, highlights, draftNotes, complete, dueRelease, releasesBefore, ref,
  };
}

// ── this repository's rules ──────────────────────────────────────────────────

const plan = createPlan(loadConfig(new URL('./release.config.json', import.meta.url)));

export const {
  CONFIG, REPO, NAME, PRODUCT_OF_REPO, ISSUE_FIELDS, ITEM_FIELDS, planByField,
  productOfRepo, parseMilestone, isIgnored, view, planFor, problems, releases, unversioned,
  milestonesOf, upgradeSection, groups, highlights, draftNotes, complete, dueRelease, releasesBefore, ref,
} = plan;
