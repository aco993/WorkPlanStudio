# 16. Promote the cost centre from a string to master data

- **Status:** Accepted
- **Date:** 2026-09-11
- **Related:** [ADR 0006](0006-explicit-browser-storage-recovery.md), which decided *against* migrations
  and is partly superseded here; [ADR 0011](0011-production-orders-own-routing-snapshots.md), whose
  "the order stops depending on master data" claim this corrects

## Context

`WorkCenter.CostCenter` was a `string`, and the repository owner asked the obvious question about the
work-centre editor: why is this typed by hand?

The field answered it itself. Five independent things pointed the same way:

- The shipped seed data already had **six distinct cost centres over seven work centres**, with
  `CC-2000` typed twice. A value several rows deliberately share is an entity, not an attribute.
- The codebase knows how to model a business key and withheld it here. `WorkCenter.Code` gets
  `NOCASE` *and* a unique index; `CostCenter` got `HasMaxLength(20)` and nothing else — so `CC-2000`,
  `cc-2000`, `CC 2000` and `CC2000` were four different cost centres.
- A cost centre is master data owned by Controlling. A work centre *selects* one; it never invents one.
- It was the only editable field in the application with **no error slot at all**, so a 21-character
  value made Save do nothing, silently.
- It was rendered as muted text and never grouped, summed or filtered, which is the only reason the
  divergence was invisible. The first time anyone asks "what does CC-2000 cost per hour in total?",
  free text breaks the answer.

The cheap option was a `<datalist>` of the distinct existing values plus `maxlength` and case
normalisation. That removes most of the divergence for an hour's work and no schema change. It was
rejected because it leaves the question unanswerable: a datalist suggests, it does not constrain, and
there is still nothing to hang a name, a status or a total on.

## Decision

A `CostCenter` entity — `Id`, `Code` (`NOCASE`, unique), `Name`, optional `Description`, `IsActive` —
with `WorkCenter.CostCenterId` as a real foreign key, `DeleteBehavior.Restrict`, its own master-data
page, and a reusable `EntityPicker` in the editor.

Three sub-decisions worth writing down:

**The foreign key is nullable.** The column it replaces allowed an empty string, and three of the
seeded work centres could plausibly have had one. A required key cannot be filled for such a row
without inventing a cost centre nobody owns, and inventing master data during an upgrade is worse than
admitting the gap. "Not assigned" is a state Controlling recognises.

**The picker is a native `<select>`.** An ARIA 1.2 combobox would have to reimplement arrow keys,
type-ahead, Home/End, Escape, `aria-activedescendant` and the mobile picker sheet, and would still be
worse with a screen reader than the control every platform already ships. What a select cannot do is
filter, and these lists are tens of rows. The component is the single place to change that if it ever
stops being true.

**A referenced-but-inactive cost centre stays in the list**, marked, rather than being hidden. Hiding
it would leave the bound value with no matching option, so opening the row and pressing Save would
silently rewrite it to "not assigned" — a data loss caused by a UI convenience.

## The migration, and what it cost

ADR 0006 decided there would be no migrations: an incompatible payload entered recovery, and the user
could export a file (that nothing could read) or reset. That is exactly the promise this change would
have broken, so the promise had to change first.

Schema 6 therefore ships with a real upgrade step. It is **not** a set of hand-written `ALTER`
statements: those have to stay identical to whatever `EnsureCreated` produces from the model, and they
stop being identical the first time someone adds a property, silently. Instead the step reads the old
database through its own frozen description of the schema-5 shape, lets EF build the current schema
from the model, and writes the rows back **with their original primary keys** — because released
orders have frozen work-centre ids into their routing snapshots, and a key that moves turns a live
shop-floor order into `MissingWorkCenter`.

Cost centres are promoted in that step: every distinct non-empty string, trimmed, de-duplicated
case-insensitively, first spelling wins. The name starts out as the code, because the old column never
held one.

Data that cannot be cleanly migrated is kept rather than dropped or guessed at:

- **An empty or whitespace cost centre** becomes `CostCenterId = null`, not a cost centre called `""`.
- **A value longer than 20 characters** (the old column had no CHECK, so one could be in there) is
  truncated to 20 rather than failing the upgrade and taking the whole database with it.
- **A snapshot naming a work centre that no longer exists** does not get a routing-index row, and the
  order is still reported the way it always was. One unmigratable order must not cost the user the
  other two hundred.
- **A payload more than one version behind, or from a newer deployment served out of a stale cache,**
  is still refused — untouched, exportable — rather than half-upgraded.

The export half was made honest at the same time: the file the recovery screen produces can now be
imported, and an older payload is upgraded on the way in.

## Consequences

- ✅ `CC-2000` is one cost centre with a name, a status and a count, and the work-centre list can be
  filtered by it. The question that motivated this is answerable.
- ✅ Referential integrity is at the database, not only in a service: `Restrict` on the foreign key
  means a cost centre that work centres book against cannot be deleted even if a guard is forgotten.
  The friendly message comes from a pre-check; the constraint is what makes the rule true.
- ✅ The validation issue that had nowhere to render is gone, because the field it belonged to is gone.
- ➖ **One more master-data screen** to keep bilingual, accessible and consistent with the others, and
  one more thing a first-time visitor has to understand before the work-centre editor makes sense.
- ➖ **Migration risk is now permanent.** Every future schema change needs its own upgrade step and its
  own test against a database built by the *old* schema — a test that quietly stops proving anything
  the moment someone generates its "old" database from the current model.
- ➖ A work centre with no cost centre is representable, so any future "cost by cost centre" report has
  to decide what to do with it rather than being able to assume a value.
- ➖ The upgrade rewrites the whole database in memory. Irrelevant against a 5 MB `localStorage`
  budget; it would not be acceptable at any real scale.
