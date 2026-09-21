#!/usr/bin/env node
// Stamps the board's Release field with the delivery a ticket went out in (docs/contributing/
// project-board.md, "Milestones and releases"): every *closed* board item that closed since the
// previous cut and carries no release yet gets the option `v<version>`, created when it does not
// exist yet. The field is empty until this runs; the milestone is the section, the field the
// delivery.
//
// Three rules, and each of them exists because a milestone holds more than one release: a release
// is cut at the end of a section and may be cut in between.
//
//   Closed only.       An open ticket has not shipped. Stamping it would claim it went out in a
//                      release that was cut while it was still being worked on.
//   Since the last     A release ships what was merged since the one before it, whatever section
//   cut.               the ticket belongs to. Scoping this by milestone — which it did — left
//                      every ticket from another section unstamped although it went out in the
//                      same cut: at the time of writing, 32 of the 34 closed tickets on the board
//                      sat in M1 and two did not, and those two would have shipped unrecorded.
//                      The section is not the delivery any more, so it cannot decide this.
//   The first stamp    A ticket goes out exactly once. Overwriting would move every earlier ticket
//   wins.              onto the newest cut, collapsing the field to "the latest release" — which
//                      the tag already says.
//
// The full version. Release notes are written per delivery, so the field has to name the
// delivery: `v2.8.0` and `v2.8.1` are two of them. A prerelease stamps the version it previews
// (`v2.8.0-rc.1` → `v2.8.0`), because the ticket ships in that line and the first-stamp rule
// then leaves the GA cut alone.
//
// Usage: node scripts/ci/stamp-release-field.mjs <tag>
// Needs a token with project scope (GH_TOKEN). Never fails the release: problems are printed.
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const ORG = 'Kuestenlogik', PROJECT = 2, FIELD = 'PVTSSF_lADOD0911s4BZcCtzhZ7lwk';
const gh = (...a) => execFileSync('gh', a, { encoding: 'utf8' });

/** The delivery a tag stands for. A prerelease stamps the version it previews. */
export const versionOf = tag => 'v' + tag.replace(/^v/, '').replace(/-.*$/, '');

/**
 * Which board items this cut stamps, and why the others are left alone. Pure, so the three rules
 * above can be pinned without a board: `items` are project items with `content.state`,
 * `content.closedAt` and the current `fieldValueByName`.
 *
 * @param items    the board's items
 * @param since    when the previous delivery was cut; everything closed after it ships in this one
 * @param version  the option to stamp, e.g. `v2.8.0`
 */
export function decide(items, since, version) {
  const after = since ? new Date(since).getTime() : 0;
  const toStamp = [], stillOpen = [], alreadyShipped = [], beforeThisCut = [];
  for (const it of items) {
    const content = it.content ?? {};
    if (content.state !== 'CLOSED') { stillOpen.push(it); continue; }
    const current = it.fieldValueByName?.name;
    if (current) { if (current !== version) alreadyShipped.push(it); continue; }
    // Closed before this cut and never stamped: history that predates the field, or a ticket the
    // board kept from an older delivery. Claiming it for this cut would be a lie the field exists
    // to prevent, so it is counted and left alone.
    const closedAt = content.closedAt ? new Date(content.closedAt).getTime() : 0;
    if (closedAt <= after) { beforeThisCut.push(it); continue; }
    toStamp.push(it);
  }
  return { toStamp, stillOpen, alreadyShipped, beforeThisCut };
}

// The board's own item list (`gh project item-list`) carries the Status column but not the issue's
// state, and the two are not the same thing — Status is a board lane somebody drags, `state` is
// whether the issue is closed. Only the second one can answer "did this ship".
const ITEMS_QUERY = `query($org: String!, $num: Int!, $cursor: String) {
  organization(login: $org) { projectV2(number: $num) { items(first: 100, after: $cursor) {
    pageInfo { hasNextPage endCursor }
    nodes {
      id
      content {
        __typename
        ... on Issue { number state closedAt milestone { title } repository { nameWithOwner } }
        ... on PullRequest { number state closedAt milestone { title } repository { nameWithOwner } }
      }
      fieldValueByName(name: "Release") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
    }
  } } }
}`;

/**
 * The delivery before this one, so "closed since" has a date. Read from the published releases
 * rather than from tags: a tag can exist without ever having been released, and the field
 * describes deliveries.
 */
export function previousCut(tag, releases = null) {
  const all = releases ?? JSON.parse(gh('api', `repos/${ORG}/Bowire/releases?per_page=100`))
    .map(r => ({ tag: r.tag_name, at: r.published_at ?? r.created_at }))
    .filter(r => r.at);
  const mine = all.find(r => r.tag === tag)?.at ?? new Date().toISOString();
  return all
    .filter(r => r.tag !== tag && new Date(r.at) < new Date(mine))
    .sort((a, b) => new Date(b.at) - new Date(a.at))[0] ?? null;
}

function allItems() {
  const nodes = [];
  let cursor = null;
  for (;;) {
    const args = ['api', 'graphql', '-f', `query=${ITEMS_QUERY}`, '-F', `org=${ORG}`, '-F', `num=${PROJECT}`];
    if (cursor) args.push('-F', `cursor=${cursor}`);
    const page = JSON.parse(gh(...args)).data.organization.projectV2.items;
    nodes.push(...page.nodes);
    if (!page.pageInfo.hasNextPage) return nodes;
    cursor = page.pageInfo.endCursor;
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const [tag] = process.argv.slice(2);
  if (!tag) { console.error('usage: stamp-release-field.mjs <tag>'); process.exit(2); }
  const version = versionOf(tag);
  try {
    const field = JSON.parse(gh('api', 'graphql', '-f', `query={ node(id: "${FIELD}") { ... on ProjectV2SingleSelectField { options { id name color description } } } }`)).data.node.options;
    let option = field.find(o => o.name === version);
    if (!option) {
      const all = [{ name: version, color: 'GRAY', description: `Ausgeliefert als ${tag}` }, ...field];
      const inner = all.map(o => `{${o.id ? `id: "${o.id}", ` : ''}name: ${JSON.stringify(o.name)}, color: ${o.color || 'GRAY'}, description: ${JSON.stringify(o.description || '')}}`).join(',');
      gh('api', 'graphql', '-f', `query=mutation { updateProjectV2Field(input: {fieldId: "${FIELD}", name: "Release", singleSelectOptions: [${inner}]}) { projectV2Field { ... on ProjectV2SingleSelectField { options { id name } } } } }`);
      option = JSON.parse(gh('api', 'graphql', '-f', `query={ node(id: "${FIELD}") { ... on ProjectV2SingleSelectField { options { id name } } } }`)).data.node.options.find(o => o.name === version);
      console.log(`created option ${version}`);
    }
    const projectId = gh('project', 'view', String(PROJECT), '--owner', ORG, '--format', 'json', '--jq', '.id').trim();
    const since = previousCut(tag);
    console.log(since
      ? `Previous cut: ${since.tag} (${since.at}) — everything closed after it ships in ${version}.`
      : `No previous cut found — every closed item without a release counts as ${version}.`);
    const { toStamp, stillOpen, alreadyShipped, beforeThisCut } =
      decide(allItems(), since?.at, version);
    for (const it of toStamp)
      gh('project', 'item-edit', '--project-id', projectId, '--id', it.id, '--field-id', FIELD, '--single-select-option-id', option.id);
    console.log(`Release ${version} stamped on ${toStamp.length} closed item(s).`);
    if (stillOpen.length) console.log(`  ${stillOpen.length} still open — they ship in a later cut.`);
    if (alreadyShipped.length) console.log(`  ${alreadyShipped.length} already carry an earlier release — left untouched.`);
    if (beforeThisCut.length) console.log(`  ${beforeThisCut.length} closed before this cut and never stamped — left alone rather than claimed.`);
  } catch (err) {
    console.log(`::warning::Release field not stamped: ${err.message}`);
  }
}
