#!/usr/bin/env node
// Stamps the board's Release field with the version a ticket shipped in (docs/contributing/
// project-board.md, "Milestones and releases"): every *closed* issue of the shipped milestones —
// in the main repo and, by mirrored milestone title, in the sibling repos on the board — gets the
// option `v<major.minor.patch>`, created when it does not exist yet. The field is empty until this
// runs; the milestone is the section, the field the delivery.
//
// Three rules, and each of them exists because a milestone holds more than one release: a release
// is cut at the end of a section and may be cut in between.
//
//   Closed only.       An open ticket has not shipped. Stamping it would claim it went out in a
//                      release that was cut while it was still being worked on.
//   The first stamp wins. A ticket goes out exactly once. Overwriting would move every earlier
//                      ticket of the section onto the newest cut, collapsing the field to "the
//                      latest release of the milestone" — which the milestone already says.
//   The full version.  Release notes are written per delivery, so the field has to name the
//                      delivery. `v2.8.0` and `v2.8.1` are two of them. A prerelease stamps the
//                      version it previews (`v2.8.0-rc.1` → `v2.8.0`), because the ticket ships
//                      in that line and the first-stamp rule then leaves the GA cut alone.
//
// Usage: node scripts/ci/stamp-release-field.mjs <tag> <milestone title>[|<milestone title>…]
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
 * `content.milestone.title` and the current `fieldValueByName`.
 */
export function decide(items, titles, version) {
  const toStamp = [], stillOpen = [], alreadyShipped = [];
  for (const it of items) {
    const content = it.content ?? {};
    const ms = content.milestone?.title;
    if (!ms || !titles.includes(ms)) continue;
    if (content.state !== 'CLOSED') { stillOpen.push(it); continue; }
    const current = it.fieldValueByName?.name;
    if (current) { if (current !== version) alreadyShipped.push(it); continue; }
    toStamp.push(it);
  }
  return { toStamp, stillOpen, alreadyShipped };
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
        ... on Issue { number state milestone { title } repository { nameWithOwner } }
        ... on PullRequest { number state milestone { title } repository { nameWithOwner } }
      }
      fieldValueByName(name: "Release") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
    }
  } } }
}`;

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
  const [tag, titlesArg] = process.argv.slice(2);
  if (!tag || !titlesArg) { console.error('usage: stamp-release-field.mjs <tag> <milestone titles |-separated>'); process.exit(2); }
  const titles = titlesArg.split('|').map(t => t.trim()).filter(Boolean);
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
    const { toStamp, stillOpen, alreadyShipped } = decide(allItems(), titles, version);
    for (const it of toStamp)
      gh('project', 'item-edit', '--project-id', projectId, '--id', it.id, '--field-id', FIELD, '--single-select-option-id', option.id);
    console.log(`Release ${version} stamped on ${toStamp.length} closed item(s) of ${titles.join(', ')}`);
    if (stillOpen.length) console.log(`  ${stillOpen.length} still open — they ship in a later cut of the same section.`);
    if (alreadyShipped.length) console.log(`  ${alreadyShipped.length} already carry an earlier release — left untouched.`);
  } catch (err) {
    console.log(`::warning::Release field not stamped: ${err.message}`);
  }
}
