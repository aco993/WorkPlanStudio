# 9. Report dispatch-rule equivalences instead of hiding them

- **Status:** Accepted
- **Date:** 2026-08-16

## Context

The Scheduling page offers six dispatch rules and four target-date rules as if
they were independent choices. They are not. With every job released at second 0
and `P` = total processing time:

| Target rule | Sets | Therefore |
| --- | --- | --- |
| TWK | `due = f · P` | strictly increasing in `P`, so **EDD ≡ SPT**; `CR = f` is constant, so **CR ≡ FIFO** |
| SLK | `due = P + s` | **EDD ≡ SPT**; `CR = (P+s)/P` decreases in `P`, so **CR ≡ LPT** |
| CON | `due = c` | all targets equal, so **EDD ≡ FIFO**; `CR = c/P` decreases in `P`, so **CR ≡ LPT** |
| NOP | `due = t · n` | keyed on operation count, the only rule that decouples all six |

On the default TWK targets the six rules therefore produce **four** distinct
schedules. A user who switches from EDD to SPT and sees a byte-identical Gantt
chart reasonably concludes the control is broken.

Three options were considered:

1. **Remove the redundant rules.** Wrong: they are not redundant in general,
   only under particular target rules. NOP decouples all six.
2. **Say nothing.** The status quo. Cheap, and quietly misleading.
3. **Report the collapse.** Tell the user which other rules would give this
   exact order.

## Decision

Option 3. `PriorityOrdering.EquivalentRules` computes the equivalent set and the
page renders it beneath the selector.

Critically, it is computed **from the orders themselves** — generate the order
under each rule and compare — not from a hard-coded table of the identities
above. A table would be faster and would drift the first time a rule's key
changed.

## Consequences

- ✅ The parameter surface stops lying. Changing a rule either changes the
  schedule or explains why it did not.
- ✅ It is self-maintaining: adding a rule, or changing a key formula,
  automatically produces correct equivalence reporting.
- ✅ `RuleEquivalenceTests` pins every identity, so the mathematics is
  documented executably rather than in a comment that can rot.
- ➖ Costs one extra priority sort per rule per run — `O(k · n log n)` for
  `k` = 6 rules. Negligible next to the search, but not free.
- ➖ Only detects *exact* order equality. Two rules that differ on one job pair
  but produce the same final schedule are not reported.

## Postscript (2026-08-16)

[ADR 0011](0011-production-orders-own-routing-snapshots.md) gave jobs real
release dates. Every identity in the table above assumes a common release at
second 0, so with staggered releases most of them dissolve — on the current
sample data none of them fires.

This vindicates the implementation choice rather than undermining it. Had the
equivalences been a hard-coded table, they would now be confidently wrong.
Computing them from the orders means the feature simply reports nothing when
nothing collapses, and starts reporting again the moment a set of orders shares
a release date.
