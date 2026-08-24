---
summary: '<one line: the decision itself, not the problem — this is what shows up in listings>'
status: Proposed
date: <YYYY-MM-DD>
---

# ADR-NNNN — <the decision, as a statement>

> **Status:** Proposed · **Date:** <YYYY-MM-DD>

## Context

What was true when this came up, and what forced a decision. Constraints
that were fixed and not up for debate belong here — they are usually the
reason the obvious option was not taken.

Write it in the past tense and do not update it later. A reader in two years
needs to know what was known *then*, not what turned out to be true.

## Decision

What was chosen, stated plainly. One paragraph is often enough.

## Alternatives considered

The part that becomes unrecoverable once the code exists — nobody can read
a rejected option out of a repository. One short block each: what it was,
and the specific reason it lost.

### <alternative>

<why not>

## Consequences

What this makes easy, and what it makes hard or impossible. Include the
costs — an ADR that lists only benefits is a sales pitch and will not help
anyone reconsidering the decision later.

## Related

- Issue #NNN — the work this came out of
- ADR-NNNN — if this supersedes or builds on another record
- `docs/architecture/<page>.md` — where the shipped result is described
