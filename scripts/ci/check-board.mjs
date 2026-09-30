#!/usr/bin/env node
// Every open ticket is planned (docs/contributing/project-board.md, "Products, releases and
// milestones"): it is on the board, and names its Product, the Release it is planned for and the
// milestone of that release — and the three agree. A ticket that does not is a release nobody
// can see coming, which is how a release ends up documented after the fact instead of planned.
//
// Two ways to fall out of the plan, both checked:
//   Not on the board. Every open issue of every product repository belongs there — there is no
//   label that opts in (the `roadmap` label is retired). With --add the sweep puts a missing
//   issue on the board itself, with its repository's product; the release and the milestone
//   are a planning decision and stay for triage, so the issue is then reported for those.
//   On the board, but its fields are missing or disagree (release-plan.mjs `problems`).
//
// Usage: node scripts/ci/check-board.mjs [--add] [--markdown]
// Exit 1 when anything is off; the list goes to stdout (Markdown with --markdown).
import { execFileSync } from 'node:child_process';
import { PRODUCT_OF_REPO, productOfRepo, problems, view } from './release-plan.mjs';

const ORG = 'Kuestenlogik', PROJECT = 2;
const gh = (...a) => execFileSync('gh', a, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
const markdown = process.argv.includes('--markdown');
const add = process.argv.includes('--add');

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
let productField = null;
function putOnBoard(name, issue) {
  if (!productField) {
    const d = JSON.parse(gh('api', 'graphql', '-f', `query={ organization(login: "${ORG}") { projectV2(number: ${PROJECT}) { id field(name: "Product") { ... on ProjectV2SingleSelectField { id options { id name } } } } } }`)).data.organization.projectV2;
    productField = { projectId: d.id, id: d.field.id, options: d.field.options };
  }
  const item = JSON.parse(gh('project', 'item-add', String(PROJECT), '--owner', ORG, '--url', issue.url, '--format', 'json'));
  const option = productField.options.find(o => o.name === productOfRepo(name));
  if (option) gh('project', 'item-edit', '--project-id', productField.projectId, '--id', item.id, '--field-id', productField.id, '--single-select-option-id', option.id);
  return item.id;
}
for (const name of Object.keys(PRODUCT_OF_REPO)) {
  let open;
  try { open = JSON.parse(gh('issue', 'list', '-R', `${ORG}/${name}`, '--state', 'open', '--limit', '1000', '--json', 'number,title,url,milestone')); }
  catch { continue; }   // a repository without issues enabled
  for (const i of open) {
    if (onBoard.has(`${ORG}/${name}#${i.number}`)) continue;
    if (!add) { offenders.push({ ref: `${name}#${i.number}`, url: i.url, title: i.title, why: ['not on the board'] }); continue; }
    putOnBoard(name, i);
    // Now on the board with its product; release and milestone are for triage.
    items.push({ repo: `${ORG}/${name}`, number: i.number, title: i.title, url: i.url, state: 'OPEN', product: productOfRepo(name), release: null, milestone: i.milestone?.title ?? null });
    console.error(`added ${name}#${i.number} to the board`);
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
