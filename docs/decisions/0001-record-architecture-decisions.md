# 0001 — Record architecture decisions

- **Status:** Accepted
- **Date:** 2026-10-15
- **Deciders:** MediStock maintainers

## Context

MediStock is a learning project that is also intended to become a usable small-business
application. Many decisions in the plan are deliberately left open ("confirm with stakeholders",
"optional", "as appropriate"), and several constraints in the plan pull against each other — for
example, "receive items into stock only when the receiving operation is confirmed" versus
"repeated submission cannot accidentally receive the same invoice twice".

Without a written record, the reasoning behind a schema constraint or a layering choice is lost
within weeks, and the next person either reverses it by accident or preserves it out of superstition.

## Decision

Every decision that is architectural — that is, expensive to reverse and not obvious from the code —
is recorded as a numbered Markdown file in `docs/decisions/`.

Each record states:

1. **Context** — the forces at play, including any plan requirement that motivates it.
2. **Decision** — what was chosen, in the active voice.
3. **Consequences** — what becomes easier, what becomes harder, and what is now expected of future code.

Rules of use:

- Records are **numbered sequentially** and never renumbered or deleted. A record that no longer
  applies is marked `Superseded by NNNN` and a new record explains the change.
- Records are **immutable once accepted**. Corrections happen by superseding.
- Records describe **why**, not how. The how lives in code and in [architecture.md](../architecture.md).
- A record is written when the decision is made, not retrospectively. A decision that is not written
  down is not decided.

Code that depends on a non-obvious decision links to its record from an XML doc comment, so a reader
in the middle of a file can find the reasoning. For example `ExpiryPolicy` points at
[0002](0002-expiry-date-interpretation.md) and the dashboard queries point at
[0003](0003-inventory-valuation.md).

## Consequences

**Easier**

- A new contributor understands the shape of the system without archaeology.
- Disagreements become specific: a record can be argued with and superseded.
- Reverting a past decision is a deliberate act with a written alternative, not an accident.

**Harder**

- Non-trivial changes cost a few extra minutes of writing.
- Records must be kept honest. A record that describes an intention the code contradicts is worse
  than no record.

**Expected of future code**

- Any PR that changes a layering rule, a schema constraint, an expiry or costing rule, or a
  security posture must add or supersede a record.
- Records are reviewed like code.
