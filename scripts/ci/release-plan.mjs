// The release plan — the rules every release script shares (docs/contributing/project-board.md,
// "Products, releases and milestones").
//
// Three things describe where a ticket goes:
//
//   Product    the artifact that ships it, and so the version line it counts in. Bowire (NuGet,
//              tool, container) counts 2.x; the VS Code extension counts its own 1.x from the
//              same repository; every plugin, SDK and companion repository counts its own line.
//   Release    the version it is planned for, in that product's count — `2.8`, `2.8.1`, `1.2`.
//              Set when the ticket is planned, not when it ships.
//   Milestone  the release it belongs to: `v2.8 — <theme>`, or `<Product> v1.1 — <theme>` for a
//              second product in the same repository. A milestone with nothing open is a
//              release that is due.
//
// Everything here is pure: board items in, decisions out. The scripts that talk to GitHub are
// thin around it, so the rules can be pinned by tests without a board.

/** Which product a repository ships by default. A repository can ship more (see `Product`). */
export const PRODUCT_OF_REPO = {
  'Bowire': 'Bowire',
  'Bowire.Protocol.Akka': 'Protocol.Akka',
  'Bowire.Protocol.Amqp': 'Protocol.Amqp',
  'Bowire.Protocol.Dis': 'Protocol.Dis',
  'Bowire.Protocol.Kafka': 'Protocol.Kafka',
  'Bowire.Protocol.Surgewave': 'Protocol.Surgewave',
  'Bowire.Protocol.TacticalApi': 'Protocol.TacticalApi',
  'Bowire.Protocol.Udp': 'Protocol.Udp',
  'Bowire.Sdk.Go': 'Sdk.Go',
  'Bowire.Sdk.Node': 'Sdk.Node',
  'Bowire.Sdk.Python': 'Sdk.Python',
  'Bowire.Sdk.Rust': 'Sdk.Rust',
  'Bowire.Templates': 'Templates',
  'Bowire.Samples': 'Samples',
  'Bowire.Bootcamp': 'Bootcamp',
  'bowire-action': 'bowire-action',
  'Bowire.VulnDb': 'VulnDb',
  'Surgewave.Diagnostics.Bowire': 'Surgewave.Diagnostics',
};

/** The default product of `owner/name` or `name`; null for a repository outside the plan. */
export function productOfRepo(repo) {
  const name = String(repo ?? '').split('/').pop();
  return PRODUCT_OF_REPO[name] ?? null;
}

/**
 * `v2.8 — Theme` → { product: null, version: '2.8', theme: 'Theme' };
 * `VS Code v1.1 — Theme` → { product: 'VS Code', version: '1.1', theme: 'Theme' };
 * anything else → null. A null product means the repository's own product.
 */
export function parseMilestone(title) {
  const m = /^(?:(.+?)\s+)?v(\d+\.\d+(?:\.\d+)?)(?:\s+[—-]\s+(.*))?$/.exec(String(title ?? '').trim());
  if (!m) return null;
  return { product: m[1] ?? null, version: m[2], theme: (m[3] ?? '').trim() || null };
}

/** `2.8.0` → `2.8` (a minor release is planned as major.minor); `2.8.1` → `2.8.1`; `v` and a prerelease suffix dropped. */
export function releaseOfVersion(version) {
  const v = String(version ?? '').replace(/^v/, '').replace(/-.*$/, '');
  const [major, minor = '0', patch = '0'] = v.split('.');
  return patch === '0' ? `${major}.${minor}` : `${major}.${minor}.${patch}`;
}

/** Whether a ticket planned for `release` ships in `version`: `2.8` and `2.8.0` in 2.8.0, `2.8.1` only in 2.8.1. */
export function releaseMatches(release, version) {
  if (!release) return false;
  return releaseOfVersion(release) === releaseOfVersion(version);
}

/** Numeric order of versions (`2.10` after `2.9`). */
export function compareVersions(a, b) {
  const pa = String(a).replace(/^v/, '').split('.').map(Number);
  const pb = String(b).replace(/^v/, '').split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d !== 0) return d;
  }
  return 0;
}

/**
 * A board item reduced to what the rules read. `raw` is a project item as the scripts query it:
 * `content` (number, state, stateReason, closedAt, milestone.title, repository.nameWithOwner),
 * `product` and `release` field values.
 */
export function view(raw) {
  const c = raw?.content ?? {};
  const repo = c.repository?.nameWithOwner ?? '';
  return {
    id: raw?.id,
    repo,
    number: c.number,
    title: c.title,
    url: c.url,
    state: c.state,
    notPlanned: c.stateReason === 'NOT_PLANNED',
    closedAt: c.closedAt ?? null,
    milestone: c.milestone?.title ?? null,
    product: raw?.product?.name ?? productOfRepo(repo),
    release: raw?.release?.text ?? null,
    type: c.issueType?.name ?? null,
    area: raw?.area?.name ?? null,
  };
}

/**
 * What a release of `product` at `version` contains: the tickets planned for it that are done,
 * those still open, and — since `since` (the previous release) — tickets that closed without
 * being planned for any release of this product, which the cut then claims. A ticket closed as
 * not planned never ships.
 */
export function planFor(items, product, version, since = null) {
  const after = since ? new Date(since).getTime() : null;
  const shipped = [], stillOpen = [], unplanned = [];
  for (const it of items) {
    if (it.product !== product) continue;
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
 * Whether a ticket's three fields agree; the reasons when they do not. Used by the daily guard:
 * an open ticket names its product, the release it is planned for, and the milestone of that
 * release.
 */
export function problems(it) {
  const out = [];
  if (it.state !== 'OPEN') return out;
  if (!it.product) out.push('no Product');
  if (!it.release) out.push('no Release');
  if (!it.milestone) { out.push('no milestone'); return out; }
  const ms = parseMilestone(it.milestone);
  if (!ms) { out.push(`milestone '${it.milestone}' names no version`); return out; }
  const msProduct = ms.product ?? productOfRepo(it.repo);
  if (it.product && msProduct !== it.product) out.push(`milestone is ${msProduct}, Product is ${it.product}`);
  if (it.release && releaseOfVersion(ms.version) !== releaseOfVersion(it.release)) {
    out.push(`milestone is v${ms.version}, Release is ${it.release}`);
  }
  return out;
}

/**
 * The open milestones of `product` in a repository, lowest version first, each with how much of
 * it is still open on the board. The first one with nothing open is the release that is due.
 */
export function releases(milestones, repo, product, items) {
  return milestones
    .filter(m => m.state === 'open')
    .map(m => ({ m, parsed: parseMilestone(m.title) }))
    .filter(x => x.parsed && (x.parsed.product ?? productOfRepo(repo)) === product)
    .map(({ m, parsed }) => {
      const planned = items.filter(it => it.product === product && releaseMatches(it.release, parsed.version));
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
