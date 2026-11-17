// Reading the plan from GitHub: the board items, the milestones, the open issues and pull requests,
// the published releases. The rules are in release-plan.mjs; this is the part that asks, shared by
// every script around them so that they ask the same question. Everything here reads; a script that
// writes does so itself, where its reader can see it. Shared by every repository that carries the
// release tool (see release-plan.mjs).
//
// Needs the gh CLI signed in, with project scope for the board (`gh auth refresh -s read:project`).
import { execFileSync } from 'node:child_process';
import { CONFIG, ISSUE_FIELDS, ITEM_FIELDS, REPO, planByField, view } from './release-plan.mjs';

const OWNER = CONFIG.board.owner, PROJECT = CONFIG.board.project;

/** Runs gh and returns its output; gh's own errors go to stderr. */
export const gh = (...a) => execFileSync('gh', a, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'], maxBuffer: 64 * 1024 * 1024 });
export const ghJson = (...a) => JSON.parse(gh(...a));

/** A GraphQL query through gh; `vars` are passed as typed variables. */
export function graphql(query, vars = {}) {
  const a = ['api', 'graphql', '-f', `query=${query}`];
  for (const [k, v] of Object.entries(vars)) a.push('-F', `${k}=${v}`);
  return ghJson(...a).data;
}

/** The board's id and the ids of the fields the scripts write (null for a field the board lacks). */
export function board() {
  const d = graphql(`{ organization(login: "${OWNER}") { projectV2(number: ${PROJECT}) { id fields(first: 50) { nodes { ... on ProjectV2FieldCommon { id name } } } } } }`);
  const p = d.organization.projectV2;
  const field = n => (n ? p.fields.nodes.find(f => f?.name === n)?.id ?? null : null);
  return { id: p.id, release: field(CONFIG.board.release) };
}

/**
 * Every issue on the board, as release-plan.mjs `view` reads it. Paged by hand: `gh api --paginate`
 * only pages a query whose cursor variable is named `$endCursor`. On a board without a Release field the
 * milestone is the plan, and an issue of this repository is planned by its milestone whether or not it
 * reached the board, so those issues are read from the repository as well.
 */
export function boardItems() {
  const items = [];
  for (let cursor = null; ;) {
    const d = graphql(`query($c: String) { organization(login: "${OWNER}") { projectV2(number: ${PROJECT}) { items(first: 100, after: $c) { pageInfo { hasNextPage endCursor } nodes { ${ITEM_FIELDS} } } } } }`, cursor ? { c: cursor } : {});
    const page = d.organization.projectV2.items;
    for (const n of page.nodes) if (n.content?.__typename === 'Issue') items.push(view(n));
    if (!page.pageInfo.hasNextPage) break;
    cursor = page.pageInfo.endCursor;
  }
  if (planByField) return items;
  const onBoard = new Set(items.map(it => `${it.repo}#${it.number}`));
  const [owner, name] = REPO.split('/');
  for (let cursor = null; ;) {
    const d = graphql(`query($c: String) { repository(owner: "${owner}", name: "${name}") { issues(first: 100, after: $c, states: [OPEN, CLOSED]) { pageInfo { hasNextPage endCursor } nodes { ${ISSUE_FIELDS} } } } }`, cursor ? { c: cursor } : {});
    const page = d.repository.issues;
    for (const content of page.nodes) if (!onBoard.has(`${REPO}#${content.number}`)) items.push(view({ content }));
    if (!page.pageInfo.hasNextPage) return items;
    cursor = page.pageInfo.endCursor;
  }
}

/** The repository's milestones, open and closed, as the REST API returns them. */
export const milestones = (repo = REPO) => ghJson('api', `repos/${repo}/milestones?state=all&per_page=100`);

/** The repository's open issues (not pull requests), with the body for markers. */
export const openIssues = (repo = REPO) =>
  ghJson('issue', 'list', '-R', repo, '--state', 'open', '--limit', '1000', '--json', 'number,title,url,milestone,body');

/** The open pull requests of the repository. */
export const openPullRequests = (repo = REPO) => ghJson('pr', 'list', '-R', repo, '--state', 'open', '--json', 'number,title');

/** The published releases of the repository as { tag, at }: a tag can exist without ever having been released. */
export const publishedReleases = (repo = REPO) => ghJson('api', `repos/${repo}/releases?per_page=100`)
  .map(r => ({ tag: r.tag_name, at: r.published_at ?? r.created_at })).filter(r => r.at);
