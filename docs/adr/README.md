---
summary: 'Architecture Decision Records — why a thing was built the way it was. Frozen at the date of the decision; never updated to match later code.'
---

# Architecture Decision Records

An ADR records **one decision**: what was chosen, what was rejected, and why.
It is written when the decision is made and then **left alone**.

That last part is the whole point, and it is what separates this directory
from the one next to it.

## Three places, three lifetimes

The three artefacts below answer three different questions. Putting them in
one place is what makes a set of documents unreadable — a reader cannot tell
whether a page describes what exists or what someone once intended.

| | Question | Lifetime |
|---|---|---|
| **Project board / [`ROADMAP.md`](../../ROADMAP.md)** | What is *going* to be built? | Until it ships or is dropped |
| **`docs/adr/`** (here) | *Why* was it built this way? | Frozen at the decision date |
| **[`docs/architecture/`](../architecture/)** | How does it work *today*? | Maintained with the code |

A piece of work therefore produces up to three artefacts over its life, and
they do **not** replace each other:

1. It starts as an **issue** on the board. That is where "we want X" lives —
   with a status, an owner and a release, none of which a markdown page has.
2. When the *how* is settled, an **ADR** is written. It captures the
   alternatives that were rejected, which is the part that becomes
   unrecoverable once the code exists.
3. When it ships, **`docs/architecture/`** describes the result.

The ADR is not rewritten in step 3. If the architecture later changes, a
**new** ADR supersedes the old one and the old one keeps its original text.

## The rule for `docs/architecture/`

**If it is not implemented, it does not go there.** No "will", no "should",
no "Design for". A design that has not shipped is a board item or a
`Proposed` ADR — never a page that reads like a description.

Every file under `docs/architecture/` carries `status: current` in its
front-matter, and CI checks it. Without that check the separation relies on
discipline, which is exactly what failed the last time: several documents
there are drafts that outlived their own implementation and now read as
descriptions of the system.

## Writing one

Copy [`_template.md`](_template.md) to `NNNN-short-kebab-title.md`, taking
the next free number. Numbers are never reused, including for superseded
records.

Keep it short. An ADR that runs long is usually two decisions, or it has
drifted into describing the implementation — which belongs in
`docs/architecture/`.

**Status values**

| | Meaning |
|---|---|
| `Proposed` | Written down, not decided yet |
| `Accepted` | Decided. The normal end state — an accepted ADR stays accepted even after the code moves on |
| `Superseded by ADR-NNNN` | A later decision replaced it. The text stays as it was |
| `Rejected` | Considered and turned down. Worth keeping: it stops the same idea coming back around |

Nothing is ever deleted. A record of a decision that turned out badly is
more useful than no record.
