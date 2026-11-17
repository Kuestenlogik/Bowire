// The release tool: scripts/ci/release-plan.mjs, release-body.mjs, resolve-milestone.mjs and the
// repository's scripts/ci/release.config.json.
//
// The same file in every Kuestenlogik repository that carries the release tool, beside that
// repository's other script tests; it finds the scripts by walking up to the repository root. The
// rules are pinned against configurations of its own (one product with a Release field, several
// products with a Product field, a board that plans by the milestone alone); the repository's own
// configuration is checked for completeness and for the files it names.
//
// Run: node --test <this folder>/*.test.mjs

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, join } from 'node:path';

let root = dirname(fileURLToPath(import.meta.url));
while (!existsSync(join(root, 'scripts', 'ci', 'release-plan.mjs'))) {
  const up = dirname(root);
  if (up === root) throw new Error('no scripts/ci/release-plan.mjs above this test');
  root = up;
}
const load = f => import(pathToFileURL(join(root, 'scripts', 'ci', f)).href);
const plan = await load('release-plan.mjs');
const body = await load('release-body.mjs');
const resolver = await load('resolve-milestone.mjs');
const {
  createPlan, validateConfig, isRelease, releaseOfVersion, releaseMatches, compareVersions, fullVersion, nextMinor,
  isTriageMilestone, notesAreDraft, hasSections, previousRelease, versionOfProps, headRefusals, notesPath,
  upgradeNotesPath, fill,
} = plan;

/** A complete configuration, changed where a case needs it. */
const config = (over = {}) => ({
  repository: 'Kuestenlogik/Sample',
  board: { owner: 'Kuestenlogik', project: 1, release: 'Release', product: null, area: 'Area', status: 'Status' },
  products: { Sample: 'Sample' },
  milestonePrefixes: [],
  required: ['Area'],
  ignore: ['<!-- roadmap-field-guard -->'],
  version: { file: 'Directory.Build.props', elements: ['Version', 'AssemblyVersion'], atCut: 'equal' },
  notes: { also: [], upgrading: 'Coming from {previous}: see [Upgrading from {previous}]({file}).' },
  pipeline: null,
  afterCut: 'bump the version past {tag}.',
  ...over,
});
const one = createPlan(config());
const many = createPlan(config({
  repository: 'Kuestenlogik/Main',
  board: { owner: 'Kuestenlogik', project: 2, release: 'Release', product: 'Product', area: 'Area', status: 'Status' },
  products: { Main: 'Main', 'Main.Plugin': 'Plugin' },
  milestonePrefixes: ['VS Code'],
  required: [],
  ignore: ['<!-- publish-failure:'],
}));
const byMilestone = createPlan(config({
  board: { owner: 'Kuestenlogik', project: 4, release: null, product: null, area: 'Area', status: 'Status' },
  required: ['Type', 'Status'],
}));

const LAST_RELEASE = '2026-09-28T20:58:20Z';
let seq = 0;
const ticket = (over = {}) => {
  const t = {
    id: 'I' + (++seq), repo: 'Kuestenlogik/Sample', number: seq, title: 't' + seq, state: 'OPEN', notPlanned: false, closedAt: null,
    milestone: 'v0.2 — Slice C', milestoneClosed: false, product: 'Sample', release: '0.2', area: 'catalog', status: 'Backlog',
    type: 'Feature', ignored: false, ...over,
  };
  return { url: `https://example.org/${t.number}`, ...t };
};

describe('the configuration', () => {
  it('of this repository is complete and names files that exist', () => {
    assert.deepEqual(validateConfig(plan.CONFIG), []);
    const c = plan.CONFIG;
    if (c.version) assert.ok(existsSync(join(root, c.version.file)), `${c.version.file} is missing`);
    for (const f of c.notes.also) assert.ok(existsSync(join(root, f)), `${f} is missing`);
    if (c.pipeline) assert.ok(existsSync(join(root, c.pipeline)), `${c.pipeline} is missing`);
    assert.equal(plan.productOfRepo(c.repository), c.products[c.repository.split('/')[1]]);
  });
  it('is complete in every sample here', () => {
    assert.deepEqual(validateConfig(config()), []);
    assert.deepEqual(validateConfig(many.CONFIG), []);
    assert.deepEqual(validateConfig(byMilestone.CONFIG), []);
  });
  it('names every key it needs, with no default in code', () => {
    const c = config();
    delete c.pipeline;
    assert.deepEqual(validateConfig(c), ['pipeline: the workflow that publishes on the tag, or null']);
    assert.ok(validateConfig(config({ products: { Other: 'Other' } })).some(p => p.startsWith('products:')));
    assert.ok(validateConfig(config({ required: ['Priority'] })).some(p => p.startsWith('required:')));
    assert.ok(validateConfig(config({ version: { file: 'x', elements: ['Version'], atCut: 'later' } })).some(p => p.startsWith('version:')));
    assert.ok(validateConfig(config({ notes: { also: [], upgrading: 'no link' } })).some(p => p.startsWith('notes:')));
    assert.deepEqual(validateConfig(config({ version: null })), []);
  });
  it('requires no field the board does not have', () => {
    const b = { owner: 'Kuestenlogik', project: 1, release: null, product: null, area: null, status: null };
    assert.deepEqual(validateConfig(config({ board: b, required: ['Area'] })), ['required names Area, but board.area is null']);
  });
});

describe('versions', () => {
  it('plans a minor release as major.minor and a patch as itself', () => {
    assert.equal(releaseOfVersion('v0.2.0'), '0.2');
    assert.equal(releaseOfVersion('0.2.1'), '0.2.1');
    assert.equal(releaseOfVersion('0.2.0-preview.1'), '0.2');
    assert.equal(releaseOfVersion('v3.0.0-rc.1'), '3.0');
  });
  it('matches 0.2 in 0.2.0, but not in the patch after it', () => {
    assert.ok(releaseMatches('0.2', 'v0.2.0'));
    assert.ok(releaseMatches('0.2.0', '0.2.0'));
    assert.ok(!releaseMatches('0.2', '0.2.1'));
    assert.ok(releaseMatches('0.2.1', 'v0.2.1'));
    assert.ok(!releaseMatches(null, '0.2.0'));
  });
  it('orders 0.10 after 0.9 and a patch after its minor release', () => {
    assert.ok(compareVersions('0.10', '0.9') > 0);
    assert.ok(compareVersions('3.0', '2.13') > 0);
    assert.ok(compareVersions('0.2.1', '0.2') > 0);
    assert.ok(compareVersions('0.2.1', '0.3') < 0);
    assert.equal(compareVersions('0.2', 'v0.2.0'), 0);
  });
  it('knows the planned form of a Release', () => {
    assert.ok(isRelease('0.2'));
    assert.ok(isRelease('0.2.1'));
    assert.ok(!isRelease('v0.2'));
    assert.ok(!isRelease('M2'));
  });
  it('writes a version in full and knows the next minor release', () => {
    assert.equal(fullVersion('0.2'), '0.2.0');
    assert.equal(fullVersion('v0.2.1'), '0.2.1');
    assert.equal(fullVersion('0.2.0-rc.1'), null);
    assert.equal(fullVersion('M1'), null);
    assert.equal(nextMinor('0.2.1'), '0.3.0');
  });
  it('reads a version element from a props file', () => {
    const props = '<PropertyGroup>\n  <Version>0.2.0-preview.1</Version>\n  <AssemblyVersion>0.2.0</AssemblyVersion>\n</PropertyGroup>';
    assert.equal(versionOfProps(props), '0.2.0-preview.1');
    assert.equal(versionOfProps(props, 'AssemblyVersion'), '0.2.0');
    assert.equal(versionOfProps(props, 'FileVersion'), null);
  });
});

describe('parseMilestone', () => {
  it('reads the version and the theme', () => {
    assert.deepEqual(one.parseMilestone('v0.2 — Slice C: formation over terrain'), { product: null, version: '0.2', theme: 'Slice C: formation over terrain' });
    assert.deepEqual(one.parseMilestone('v0.2.1 - Fixes'), { product: null, version: '0.2.1', theme: 'Fixes' });
    assert.deepEqual(one.parseMilestone('v0.3'), { product: null, version: '0.3', theme: null });
  });
  it('reads a second product of the repository where it is configured', () => {
    assert.deepEqual(many.parseMilestone('VS Code v1.1 — Marketplace publish'), { product: 'VS Code', version: '1.1', theme: 'Marketplace publish' });
    assert.equal(one.parseMilestone('VS Code v1.1 — Marketplace publish'), null);
  });
  it('is nothing for a title that names no release', () => {
    assert.equal(one.parseMilestone('M2 — Slice C'), null);
    assert.equal(one.parseMilestone('attempt-1 · v0.3 — Localisation'), null);
    assert.equal(one.parseMilestone('Triage — new CVEs'), null);
    assert.equal(one.parseMilestone(null), null);
  });
  it('knows a triage milestone', () => {
    assert.ok(isTriageMilestone('Triage'));
    assert.ok(isTriageMilestone('Triage — new CVEs'));
    assert.ok(isTriageMilestone('Triage - new CVEs'));
    assert.ok(!isTriageMilestone('v2.8 — Triage'));
    assert.ok(!isTriageMilestone('Triaged'));
    assert.ok(!isTriageMilestone('Triage —'));
    assert.ok(!isTriageMilestone(null));
  });
});

describe('view', () => {
  const raw = (over = {}) => ({
    id: 'X', release: { text: ' 0.2 ' }, area: { name: 'ui' }, status: { name: 'Backlog' },
    content: { number: 7, state: 'OPEN', stateReason: null, body: 'Text', issueType: { name: 'Bug' }, repository: { nameWithOwner: 'Kuestenlogik/Sample' }, milestone: { title: 'v0.2 — C', state: 'OPEN' } },
    ...over,
  });
  it('reads a board item', () => {
    const v = one.view(raw());
    assert.equal(v.release, '0.2');
    assert.equal(v.area, 'ui');
    assert.equal(v.type, 'Bug');
    assert.equal(v.product, 'Sample');
    assert.equal(v.milestoneClosed, false);
    assert.equal(v.ignored, false);
    assert.deepEqual(one.problems(v), []);
  });
  it('reads an empty Release as none', () => {
    assert.equal(one.view({ release: { text: '' }, content: {} }).release, null);
    assert.equal(one.view({ content: {} }).release, null);
  });
  it('takes the product of the repository where the board names none', () => {
    assert.equal(many.view(raw({ content: { repository: { nameWithOwner: 'Kuestenlogik/Main.Plugin' } } })).product, 'Plugin');
    assert.equal(many.view(raw({ product: { name: 'VS Code' } })).product, 'VS Code');
    assert.equal(many.productOfRepo('Kuestenlogik/Unrelated'), null);
  });
  it('takes the Release from the milestone on a board without the field', () => {
    assert.equal(byMilestone.view(raw({ release: undefined })).release, '0.2');
    assert.equal(byMilestone.view(raw({ release: { text: '0.9' } })).release, '0.2');
    assert.equal(byMilestone.view(raw({ content: { milestone: { title: 'M1 — Catalogue' } } })).release, null);
  });
  it("reads an issue that is not on the board, its Area from the board else from its area: label", () => {
    const v = byMilestone.view({ content: { number: 9, state: 'CLOSED', repository: { nameWithOwner: 'Kuestenlogik/Sample' }, milestone: { title: 'v0.7 — Hygiene', state: 'OPEN' }, labels: { nodes: [{ name: 'roadmap' }, { name: 'area:broker' }] } } });
    assert.deepEqual([v.release, v.area, v.product], ['0.7', 'broker', 'Sample']);
    assert.equal(one.view(raw({ content: { labels: { nodes: [{ name: 'area:broker' }] } } })).area, 'ui');
  });
  it('knows a tracking issue by its marker', () => {
    assert.equal(many.view(raw({ content: { body: 'x <!-- publish-failure:winget --> y' } })).ignored, true);
    assert.equal(one.view(raw({ content: { body: 'A ticket about publish failures.' } })).ignored, false);
    assert.equal(one.view(raw({ content: {} })).ignored, false);
  });
  it('asks the board for the fields the configuration names, and only those', () => {
    assert.match(one.ITEM_FIELDS, /release: fieldValueByName\(name: "Release"\)/);
    assert.ok(!one.ITEM_FIELDS.includes('product:'));
    assert.match(many.ITEM_FIELDS, /product: fieldValueByName\(name: "Product"\)/);
    assert.ok(!byMilestone.ITEM_FIELDS.includes('release:'));
  });
});

describe('planFor', () => {
  it('ships the done tickets of this product and release, and names the open ones', () => {
    const items = [
      ticket({ state: 'CLOSED', closedAt: '2026-09-30T00:00:00Z' }),
      ticket(),                                               // still open
      ticket({ state: 'CLOSED', release: '0.3' }),            // another release
      ticket({ state: 'CLOSED', release: '0.2.1' }),          // the patch after it
      ticket({ state: 'CLOSED', notPlanned: true }),          // never shipped
      ticket({ state: 'CLOSED', product: 'Other' }),          // another product's 0.2
    ];
    const r = one.planFor(items, 'Sample', '0.2.0', LAST_RELEASE);
    assert.deepEqual(r.shipped.map(x => x.number), [items[0].number]);
    assert.deepEqual(r.stillOpen.map(x => x.number), [items[1].number]);
    assert.deepEqual(r.unplanned, []);
  });
  it('plans a patch release on its own', () => {
    const fix = ticket({ state: 'CLOSED', release: '0.2.1', milestone: 'v0.2.1 — Fixes' });
    assert.deepEqual(one.planFor([fix, ticket({ state: 'CLOSED' })], 'Sample', '0.2.1', LAST_RELEASE).shipped.map(x => x.number), [fix.number]);
  });
  it('claims what closed since the previous release without a plan, and only that', () => {
    const before = ticket({ state: 'CLOSED', release: null, closedAt: '2026-09-20T00:00:00Z' });
    const after = ticket({ state: 'CLOSED', release: null, closedAt: '2026-09-30T00:00:00Z' });
    const dropped = ticket({ state: 'CLOSED', release: null, closedAt: '2026-09-30T00:00:00Z', notPlanned: true });
    const tracker = ticket({ state: 'CLOSED', release: null, closedAt: '2026-09-30T00:00:00Z', ignored: true });
    assert.deepEqual(one.planFor([before, after, dropped, tracker], 'Sample', '0.2.0', LAST_RELEASE).unplanned.map(x => x.number), [after.number]);
  });
  it('claims nothing without a previous release to count from', () => {
    assert.deepEqual(one.planFor([ticket({ state: 'CLOSED', release: null, closedAt: '2026-09-30T00:00:00Z' })], 'Sample', '0.2.0', null).unplanned, []);
  });
});

describe('problems', () => {
  it('is nothing when the plan is complete and agrees', () => {
    assert.deepEqual(one.problems(ticket()), []);
    assert.deepEqual(one.problems(ticket({ release: '0.2.1', milestone: 'v0.2.1 — Fixes' })), []);
    assert.deepEqual(many.problems(ticket({ repo: 'Kuestenlogik/Main', product: 'VS Code', release: '1.1', milestone: 'VS Code v1.1 — X' })), []);
    assert.deepEqual(many.problems(ticket({ repo: 'Kuestenlogik/Main.Plugin', product: 'Plugin', release: '1.2', milestone: 'v1.2 — X' })), []);
  });
  it('names what is missing', () => {
    assert.deepEqual(one.problems(ticket({ area: null })), ['no Area']);
    assert.deepEqual(one.problems(ticket({ release: null })), ['no Release']);
    assert.deepEqual(one.problems(ticket({ milestone: null })), ['no milestone']);
    assert.deepEqual(one.problems(ticket({ area: null, release: null, milestone: null })), ['no Area', 'no Release', 'no milestone']);
    assert.deepEqual(many.problems(ticket({ repo: 'Kuestenlogik/Unrelated', product: null, milestone: 'v0.2 — C' })), ['no Product']);
  });
  it('names what disagrees', () => {
    assert.deepEqual(one.problems(ticket({ release: '0.3' })), ['milestone is v0.2, Release is 0.3']);
    assert.deepEqual(one.problems(ticket({ release: '0.2.1' })), ['milestone is v0.2, Release is 0.2.1']);
    assert.deepEqual(one.problems(ticket({ release: 'v0.2' })), ["Release 'v0.2' is not a version such as 0.2 or 0.2.1"]);
    assert.deepEqual(one.problems(ticket({ milestone: 'M2 — Slice C' })), ["milestone 'M2 — Slice C' names no release"]);
    assert.deepEqual(many.problems(ticket({ repo: 'Kuestenlogik/Main', product: 'VS Code', milestone: 'v0.2 — C' })), ['milestone is Main, Product is VS Code']);
  });
  it('names an open ticket in a release that has shipped', () => {
    assert.deepEqual(one.problems(ticket({ release: '0.1', milestone: 'v0.1 — Slice A', milestoneClosed: true })), ['milestone v0.1 is closed: that release has shipped']);
  });
  it('takes a ticket in the triage milestone as not planned yet', () => {
    const triage = { repo: 'Kuestenlogik/Main.Plugin', product: 'Plugin', milestone: 'Triage — new CVEs' };
    assert.deepEqual(many.problems(ticket({ ...triage, release: null })), []);
    assert.deepEqual(many.problems(ticket({ ...triage, release: null, product: null })), ['no Product']);
    assert.deepEqual(many.problems(ticket({ ...triage, release: '1.4' })), ['milestone is Triage — new CVEs, Release is 1.4: move it to the milestone of that release']);
  });
  it('names the board fields the configuration requires', () => {
    assert.deepEqual(byMilestone.problems(ticket({ release: '0.2' })), []);
    assert.deepEqual(byMilestone.problems(ticket({ type: null, status: null })), ['no Type', 'no Status']);
    assert.deepEqual(byMilestone.problems(ticket({ status: 'Done' })), ['open, but Done on the board']);
  });
  it('plans by the milestone alone where the board has no Release field', () => {
    assert.deepEqual(byMilestone.problems(ticket({ release: null, milestone: null })), ['no milestone']);
    assert.deepEqual(byMilestone.problems(ticket({ release: null, milestone: 'M1 — Catalogue' })), ["milestone 'M1 — Catalogue' names no release"]);
  });
  it('leaves closed tickets and tracking issues alone', () => {
    assert.deepEqual(one.problems(ticket({ state: 'CLOSED', release: null, milestone: null, area: null })), []);
    assert.deepEqual(one.problems(ticket({ release: null, milestone: null, ignored: true })), []);
  });
});

describe('releases and the release order', () => {
  const milestones = [
    { number: 9, title: 'v0.10 — J', state: 'open', open_issues: 1 },
    { number: 4, title: 'v0.3 — D', state: 'open', open_issues: 0 },
    { number: 3, title: 'v0.2 — C', state: 'open', open_issues: 1 },
    { number: 1, title: 'v0.1 — A', state: 'closed', open_issues: 0 },
    { number: 2, title: 'attempt-1 · v0.3 — old', state: 'open', open_issues: 0 },
    { number: 5, title: 'M1 — Sections', state: 'open', open_issues: 2 },
    { number: 6, title: 'Triage — new', state: 'open', open_issues: 1 },
  ];
  const items = [
    ticket({ state: 'CLOSED', milestone: 'v0.2 — C' }),
    ticket({ milestone: 'v0.2 — C' }),
    ticket({ state: 'CLOSED', release: '0.3', milestone: 'v0.3 — D' }),
    ticket({ release: '0.10', milestone: 'v0.10 — J' }),
  ];
  const list = one.releases(milestones, items);

  it('lists the open releases, lowest first, with what is open and done', () => {
    assert.deepEqual(list.map(r => r.version), ['0.2', '0.3', '0.10']);
    assert.deepEqual(list.map(r => r.open), [1, 0, 1]);
    assert.deepEqual(list.map(r => r.done), [1, 1, 0]);
  });
  it('makes only the lowest open release due: a later one with nothing open waits', () => {
    assert.equal(one.dueRelease(list), null);
    assert.equal(one.dueRelease(one.releases(milestones.filter(m => m.title !== 'v0.2 — C'), items)).version, '0.3');
  });
  it('is not due while its milestone holds an open issue the board does not plan for it', () => {
    assert.equal(one.dueRelease(one.releases([{ title: 'v0.3 — D', state: 'open', open_issues: 1 }], items)), null);
  });
  it('is not due when nothing was done for it', () => {
    assert.equal(one.dueRelease(one.releases([{ title: 'v0.4 — B', state: 'open', open_issues: 0 }], items)), null);
  });
  it('lets a patch of a shipped release go before the next minor release', () => {
    const ms = [{ title: 'v0.2 — C', state: 'open', open_issues: 1 }, { title: 'v0.1.1 — Fixes', state: 'open', open_issues: 0 }];
    const l = one.releases(ms, [...items, ticket({ state: 'CLOSED', release: '0.1.1', milestone: 'v0.1.1 — Fixes' })]);
    assert.equal(one.dueRelease(l).version, '0.1.1');
    assert.deepEqual(one.releasesBefore(l, '0.1.1'), []);
    assert.deepEqual(one.releasesBefore(l, '0.2.0').map(r => r.version), ['0.1.1']);
  });
  it('counts the releases of one product only', () => {
    const ms = [{ title: 'v2.8 — A', state: 'open', open_issues: 0 }, { title: 'VS Code v1.1 — D', state: 'open', open_issues: 1 }];
    const mixed = [ticket({ product: 'Main', release: '2.8', state: 'CLOSED' }), ticket({ product: 'VS Code', release: '1.1' })];
    assert.deepEqual(many.releases(ms, mixed, 'Main').map(r => [r.version, r.open, r.done]), [['2.8', 0, 1]]);
    assert.deepEqual(many.releases(ms, mixed, 'VS Code').map(r => [r.version, r.open, r.done]), [['1.1', 1, 0]]);
  });
  it('names the open milestones that name no release, triage aside', () => {
    assert.deepEqual(one.unversioned(milestones).map(m => m.number), [2, 5]);
  });
  it('finds the milestones a version ships, a patch by its own', () => {
    const ms = [{ number: 1, title: 'v0.3 — A' }, { number: 2, title: 'v0.3.1 — Fixes' }, { number: 3, title: 'VS Code v0.3 — X' }];
    assert.deepEqual(many.milestonesOf(ms, 'Main', 'v0.3.0').map(m => m.number), [1]);
    assert.deepEqual(many.milestonesOf(ms, 'Main', '0.3.1').map(m => m.number), [2]);
    assert.deepEqual(many.milestonesOf(ms, 'VS Code', 'v0.3.0-rc.1').map(m => m.number), [3]);
    assert.deepEqual(many.milestonesOf(ms, 'Main', 'v0.9.0'), []);
  });
});

describe('the commit a cut tags', () => {
  const main = '7cbe9951a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7';
  it('is origin/main with no pull request open', () => {
    assert.deepEqual(headRefusals(main, main, []), []);
  });
  it('refuses a HEAD that is not origin/main', () => {
    assert.deepEqual(headRefusals('343d04b0aa', main, []), ['HEAD is 343d04b, origin/main is 7cbe995: cut from main as GitHub has it.']);
  });
  it('refuses while a pull request is open', () => {
    assert.deepEqual(headRefusals(main, main, [{ number: 180 }, { number: 181 }]), ['2 pull request(s) open (#180, #181): merge or close them first.']);
  });
  it('counts from the previous release, skipping prereleases and other tags', () => {
    const tags = [
      { tag: 'attempt-1', at: '2026-09-27T00:00:00Z' },
      { tag: 'v0.1.0', at: LAST_RELEASE },
      { tag: 'v0.2.0-rc.1', at: '2026-09-29T00:00:00Z' },
      { tag: 'v0.2.0', at: '2026-09-30T00:00:00Z' },
    ];
    assert.equal(previousRelease('v0.2.0', tags).tag, 'v0.1.0');
    assert.equal(previousRelease('v0.2.0-rc.1', tags).tag, 'v0.1.0');
    assert.equal(previousRelease('v0.3.0', tags).tag, 'v0.2.0');
    assert.equal(previousRelease('v0.1.0', tags), null);
    assert.equal(previousRelease(null, tags).tag, 'v0.2.0');
  });
});

describe('drafted notes', () => {
  const shipped = [
    ticket({ number: 12, title: 'Formations on the map', area: 'ui', state: 'CLOSED' }),
    ticket({ number: 3, title: 'A slot that rings', area: 'catalog', type: 'Bug', state: 'CLOSED' }),
    ticket({ number: 5, title: 'No area', area: null, type: 'Task', state: 'CLOSED' }),
  ];
  const draft = (over = {}) => one.draftNotes({
    product: 'Sample', version: '0.2.0', release: '0.2', milestones: [{ title: 'v0.2 — Slice C' }], shipped, unplanned: [],
    since: 'v0.1.0', commits: 42, upgrading: false, ...over,
  });
  it('opens with the front matter, the draft marker and the frame', () => {
    const md = draft();
    assert.ok(md.startsWith('---\n# The release'));
    assert.match(md, /\ntitle: <fill in before the tag>\nversion: 0\.2\.0\n---\n/);
    assert.match(md, /<!-- draft: written by scripts\/ci\/release\.mjs notes from the plan: Release 0\.2, milestone v0\.2 — Slice C\./);
    assert.match(md, /3 planned ticket\(s\) done, 0 closed without a plan since v0\.1\.0\. 42 commit\(s\) since v0\.1\.0\./);
    assert.match(md, /<One paragraph: what Sample 0\.2\.0 is about\.>/);
    assert.ok(notesAreDraft(md));
  });
  it('groups the tickets by area, a ticket without one under other, and marks what is no feature', () => {
    const md = draft();
    assert.deepEqual([...md.matchAll(/^## (.+)$/gm)].map(m => m[1]), ['catalog', 'other', 'ui']);
    assert.ok(md.includes('- A slot that rings ([#3](https://example.org/3)) _(Bug)_'));
    assert.ok(md.includes('- Formations on the map ([#12](https://example.org/12))\n'));
    assert.ok(md.includes('### <headline>'));
  });
  it('names the product on a board with a Product field, and a ticket of another repository by it', () => {
    const md = many.draftNotes({ product: 'Main', version: '2.8.0', release: '2.8', milestones: [], shipped: [ticket({ repo: 'Kuestenlogik/Main.Plugin', number: 4, state: 'CLOSED' })], unplanned: [], since: null, commits: null, upgrading: false });
    assert.match(md, /from the plan: Product Main, Release 2\.8, no milestone\./);
    assert.match(md, /closed without a plan since the start\.\n/);
    assert.ok(md.includes('([Kuestenlogik/Main.Plugin#4]('));
  });
  it('links the upgrade notes from the previous release where they are written', () => {
    assert.equal(upgradeNotesPath('v0.1.0'), 'docs/release-notes/upgrading-from-v0.1.0.md');
    const section = one.upgradeSection('v0.1.0', true);
    assert.equal(section, '## Upgrading\n\nComing from v0.1.0: see [Upgrading from v0.1.0](upgrading-from-v0.1.0.md).\n\n');
    assert.ok(draft({ upgrading: true }).includes(section));
    assert.equal(one.upgradeSection('v0.1.0', false), '');
    assert.equal(one.upgradeSection(null, true), '');
  });
  it('prints the grouped list alone for a pipeline to fill in', () => {
    assert.equal(one.highlights(shipped), '### catalog\n\n- A slot that rings ([#3](https://example.org/3)) _(Bug)_\n\n### other\n\n- No area ([#5](https://example.org/5)) _(Task)_\n\n### ui\n\n- Formations on the map ([#12](https://example.org/12))\n');
  });
  it('are written to docs/release-notes/v<version>.md', () => {
    assert.equal(notesPath('v0.2.0'), 'docs/release-notes/v0.2.0.md');
  });
});

describe('notes a cut accepts', () => {
  const done = '---\ntitle: Formations march over terrain\nversion: 0.2.0\n---\n\nThe paragraph.\n\n## ui\n\n### Formations on the map\n\nProse <b>here</b>.\n';
  it('are no draft once the marker and the placeholders are gone', () => {
    assert.ok(!notesAreDraft(done));
    assert.ok(hasSections(done));
  });
  it('are a draft while anything of the draft is left', () => {
    assert.ok(notesAreDraft(done.replace('The paragraph.', '<!-- draft: written by scripts/ci/release.mjs -->')));
    assert.ok(notesAreDraft(done.replace('The paragraph.', '<One paragraph: what Sample 0.2.0 is about.>')));
    assert.ok(notesAreDraft(done.replace('### Formations on the map', '### <headline>')));
    assert.ok(notesAreDraft(done.replace('Formations march over terrain', '<fill in before the tag>')));
    assert.ok(notesAreDraft(done.replace('Formations march over terrain', 'Sample v0.2.0')));
    assert.ok(notesAreDraft(done.replace('### Formations on the map', 'Formations on the map')));
    assert.ok(notesAreDraft('## ui\n\n### Formations on the map\n'));
  });
  it('take a placeholder inside a curation hint for no draft, as it is never published', () => {
    assert.ok(!notesAreDraft(done.replace('The paragraph.', '<!-- Pattern: ### <headline> (#issue) -->\nThe paragraph.')));
  });
  it('fills the configured sentences', () => {
    assert.equal(fill('past {tag}, then {next}; {unknown}', { tag: 'v0.2.0', next: '0.3.0' }), 'past v0.2.0, then 0.3.0; {unknown}');
  });
});

describe('release-body', () => {
  const { stripFrontMatter, frontMatterTitle, isPlaceholderTitle, compose, releaseName } = body;
  const notes = (title, text = '## The map\n\n### A path\n\nText.\n') => `---\ntitle: ${title}\nversion: 2.7.0\n---\n\n${text}`;
  it('names the release by its version alone, a prerelease whole', () => {
    assert.equal(releaseName('v2.7.0'), '2.7.0');
    assert.equal(releaseName('2.7.0'), '2.7.0');
    assert.equal(releaseName('v3.0.0-rc.1'), '3.0.0-rc.1');
  });
  it('removes the front matter, and leaves a body without one or with a broken one', () => {
    assert.equal(stripFrontMatter(notes('A title')), '## The map\n\n### A path\n\nText.\n');
    assert.equal(stripFrontMatter('## Only a body\n'), '## Only a body\n');
    const broken = '---\ntitle: no end marker\n\n## The map\n';
    assert.equal(stripFrontMatter(broken), broken);
    assert.equal(stripFrontMatter(notes('A title').replace(/\n/g, '\r\n')), '## The map\n\n### A path\n\nText.\n');
  });
  it("reads the delivery's own title, without the quotes a YAML writer may have used", () => {
    assert.equal(frontMatterTitle(notes('A map you can read')), 'A map you can read');
    assert.equal(frontMatterTitle(notes('"Quoted, because of the comma"')), 'Quoted, because of the comma');
    assert.equal(frontMatterTitle('## The map\n'), null);
  });
  it('catches a title that says nothing the tag does not', () => {
    for (const t of ['<fill in before the tag>', '', '   ', 'Bowire v2.6.0', 'Terracle v0.3.0', 'v2.6.0', '2.6']) assert.ok(isPlaceholderTitle(t), t);
    assert.ok(!isPlaceholderTitle('Storage isolation, and a rate limiter that throttled v2.6'));
    assert.ok(!isPlaceholderTitle('A map you can read'));
  });
  it("heads the body with the delivery's title, the sections at the level they were written", () => {
    const out = compose(notes('A map you can read'));
    assert.ok(out.startsWith('# A map you can read\n\n## The map'), out.slice(0, 60));
    assert.equal((out.match(/^# /gm) ?? []).length, 1);
    assert.ok(out.includes('\n### A path'));
    assert.ok(!out.includes('\n\n\n'));
  });
  it('adds no second title, and no placeholder as one', () => {
    const own = compose(notes('Front-matter title', '# The body brought its own\n\nText.\n'));
    assert.ok(own.startsWith('# The body brought its own'));
    assert.equal((own.match(/^# /gm) ?? []).length, 1);
    assert.ok(compose(notes('Bowire v2.6.0')).startsWith('## The map'));
  });
});

describe('resolve-milestone', () => {
  it('names the milestone, its theme and its number, or nothing', () => {
    const out = resolver.outputs([{ number: 19, title: 'v2.8 — Localisation and the agent hub' }, { number: 30, title: 'v2.8 — Second' }]);
    assert.deepEqual(out, { milestones: 'v2.8 — Localisation and the agent hub|v2.8 — Second', milestone: 'v2.8 — Localisation and the agent hub', theme: 'Localisation and the agent hub + Second', numbers: '19 30' });
    assert.deepEqual(resolver.outputs([]), { milestones: '', milestone: '', theme: '', numbers: '' });
  });
  it("finds this repository's release by its version", () => {
    const ms = [{ number: 1, title: 'v0.3 — A' }, { number: 2, title: 'v0.3.1 — Fixes' }, { number: 3, title: 'M1 — Old' }];
    assert.deepEqual(resolver.resolve('v0.3.0', ms).map(m => m.number), [1]);
    assert.deepEqual(resolver.resolve('v0.3.1', ms).map(m => m.number), [2]);
    assert.deepEqual(resolver.resolve('v0.9.0', ms), []);
  });
});
