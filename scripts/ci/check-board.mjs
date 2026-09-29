#!/usr/bin/env node
// Every open ticket is planned (docs/contributing/project-board.md, "Products, releases and
// milestones"): it is on the board, and names its Product, the Release it is planned for and the
// milestone of that release — and the three agree. A ticket that does not is a release nobody
// can see coming, which is how a release ends up documented after the fact instead of planned.
//
// Two ways to fall out of the plan, both checked:
//   Not on the board. The board only adds issues labelled `roadmap` on its own, so an issue filed
//   without the label never appears in a plan. Every repository of a product is swept.
//   On the board, but its fields are missing or disagree (release-plan.mjs `problems`).
//
// Usage: node scripts/ci/check-board.mjs [--markdown]
// Exit 1 when anything is off; the list goes to stdout (Markdown with --markdown).
import { execFileSync } from 'node:child_process';
import { PRODUCT_OF_REPO, problems, view } from './release-plan.mjs';

const ORG = 'Kuestenlogik', PROJECT = 2;
const gh = (...a) => execFileSync('gh', a, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
const markdown = process.argv.includes('--markdown');

const ITEMS = `query($cursor: String) { organization(login: "${ORG}") { projectV2(number: ${PROJECT}) { items(first: 100, after: $cursor) {
  pageInfo { hasNextPage endCursor }
  nodes { id
    product: fieldValueByName(name: "Product") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
    release: fieldValueByName(name: "Release") { ... on ProjectV2ItemFieldTextValue { text } }
    content { __typename ... on Issue { number title url state stateReason closedAt milestone { title } repository { nameWithOwner } } } } } } } }`;

const items = [];
for (let cursor = null; ;) {
  const args = ['api', 'graphql', '-f', `query=${ITEMS}`];
  if (cursor) args.push('-F', `cursor=${cursor}`);
  const page = JSON.parse(gh(...args)).data.organization.projectV2.items;
  items.push(...page.nodes.filter(n => n.content?.__typename === 'Issue').map(view));
  if (!page.pageInfo.hasNextPage) break;
  cursor = page.pageInfo.endCursor;
}
const onBoard = new Set(items.map(it => `${it.repo}#${it.number}`));

const offenders = [];
for (const name of Object.keys(PRODUCT_OF_REPO)) {
  let open;
  try { open = JSON.parse(gh('issue', 'list', '-R', `${ORG}/${name}`, '--state', 'open', '--limit', '1000', '--json', 'number,title,url')); }
  catch { continue; }   // a repository without issues enabled
  for (const i of open) {
    if (!onBoard.has(`${ORG}/${name}#${i.number}`)) offenders.push({ ref: `${name}#${i.number}`, url: i.url, title: i.title, why: ['not on the board'] });
  }
}
for (const it of items) {
  const why = problems(it);
  if (why.length) offenders.push({ ref: `${it.repo.split('/')[1]}#${it.number}`, url: it.url, title: it.title, why });
}

if (offenders.length === 0) {
  console.log(markdown ? '✅ Every open ticket is on the board and names its Product, Release and milestone.' : 'ok');
  process.exit(0);
}
for (const o of offenders) {
  console.log(markdown ? `- [\`${o.ref}\`](${o.url}) ${o.title} — ${o.why.join('; ')}` : `${o.ref}\t${o.why.join('; ')}\t${o.title}`);
}
process.exit(1);
