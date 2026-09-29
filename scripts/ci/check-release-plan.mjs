#!/usr/bin/env node
// After a tag: does the board say what this release shipped? (docs/contributing/project-board.md,
// "Products, releases and milestones").
//
// The release is planned — every ticket carries its Product and the Release it is planned for —
// so there is nothing to stamp. What can still be off, and what this reports:
//
//   A ticket planned for this release is still open. `release.mjs cut` refuses that; a tag pushed
//   by hand does not. It did not ship, and the log says so.
//   A ticket closed since the previous release without being planned. It went out in this one,
//   so it gets this release (`2.8` for v2.8.0, `2.8.1` for v2.8.1) — the one case where the
//   field is written after the fact, and the log lists each such ticket so the plan can learn.
//
// A prerelease claims nothing: its tickets are claimed by the release it previews. Never fails
// the release — problems are printed as warnings.
//
// Usage: node scripts/ci/check-release-plan.mjs <tag>
// Needs a token with project scope (GH_TOKEN).
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { planFor, productOfRepo, releaseOfVersion, view } from './release-plan.mjs';

const ORG = 'Kuestenlogik', PROJECT = 2;
const gh = (...a) => execFileSync('gh', a, { encoding: 'utf8' });

/**
 * The release before this one, so "closed since" has a date. Read from the published releases
 * rather than from tags: a tag can exist without ever having been released.
 */
export function previousCut(tag, releases) {
  const mine = releases.find(r => r.tag === tag)?.at ?? new Date().toISOString();
  return releases
    .filter(r => r.tag !== tag && !/-/.test(r.tag) && new Date(r.at) < new Date(mine))
    .sort((a, b) => new Date(b.at) - new Date(a.at))[0] ?? null;
}

const ITEMS = `query($org: String!, $num: Int!, $cursor: String) {
  organization(login: $org) { projectV2(number: $num) { id items(first: 100, after: $cursor) {
    pageInfo { hasNextPage endCursor }
    nodes { id
      product: fieldValueByName(name: "Product") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
      release: fieldValueByName(name: "Release") { ... on ProjectV2ItemFieldTextValue { text } }
      content { __typename ... on Issue { number title state stateReason closedAt milestone { title } repository { nameWithOwner } } }
    }
  } } }
}`;

function allItems() {
  const nodes = [];
  let cursor = null, projectId = null;
  for (;;) {
    const args = ['api', 'graphql', '-f', `query=${ITEMS}`, '-F', `org=${ORG}`, '-F', `num=${PROJECT}`];
    if (cursor) args.push('-F', `cursor=${cursor}`);
    const p = JSON.parse(gh(...args)).data.organization.projectV2;
    projectId = p.id;
    nodes.push(...p.items.nodes.filter(n => n.content?.__typename === 'Issue'));
    if (!p.items.pageInfo.hasNextPage) return { projectId, items: nodes.map(view) };
    cursor = p.items.pageInfo.endCursor;
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const [tag] = process.argv.slice(2);
  if (!tag) { console.error('usage: check-release-plan.mjs <tag>'); process.exit(2); }
  try {
    const repo = process.env.GITHUB_REPOSITORY ?? JSON.parse(gh('repo', 'view', '--json', 'nameWithOwner')).nameWithOwner;
    const product = productOfRepo(repo);
    const release = releaseOfVersion(tag);
    const prerelease = /-/.test(tag);
    const releases = JSON.parse(gh('api', `repos/${repo}/releases?per_page=100`))
      .map(r => ({ tag: r.tag_name, at: r.published_at ?? r.created_at })).filter(r => r.at);
    const since = previousCut(tag, releases);
    const { projectId, items } = allItems();
    const { shipped, stillOpen, unplanned } = planFor(items, product, tag, since?.at);
    console.log(`${product} ${tag}: ${shipped.length} planned ticket(s) shipped${since ? ` (previous release ${since.tag})` : ''}.`);
    for (const it of stillOpen) console.log(`::warning::${it.repo}#${it.number} is planned for ${release} but still open — it did not ship in ${tag}.`);
    if (unplanned.length && !prerelease) {
      const field = JSON.parse(gh('api', 'graphql', '-f', `query={ organization(login: "${ORG}") { projectV2(number: ${PROJECT}) { field(name: "Release") { ... on ProjectV2FieldCommon { id } } } } }`)).data.organization.projectV2.field.id;
      for (const it of unplanned) {
        gh('project', 'item-edit', '--project-id', projectId, '--id', it.id, '--field-id', field, '--text', release);
        console.log(`::warning::${it.repo}#${it.number} closed since ${since?.tag ?? 'the start'} without a planned release — set to ${release}.`);
      }
    }
  } catch (err) {
    console.log(`::warning::Release plan not checked: ${err.message}`);
  }
}
