# Defending this project in an interview

Written to be *used*, not admired. It states what the project is, which decisions
are worth defending, where it is weak, and what to say when someone pushes.

A note on register: the strongest thing you can do in a technical interview is
say "I measured it" and then say the number. The second strongest is "I don't
know, here's how I'd find out." Both beat confident hand-waving, and this
project gives you material for the first.

---

## 1. What the application does

Manufacturing routings — *Arbeitspläne* — and what they cost.

A **work plan** is the ordered list of operations needed to make a part: saw the
bar, turn it, mill the keyway, grind, inspect. Each **operation** runs on a
**work center** (a machine or manual station) and has a one-off setup time plus
a per-piece run time. Multiply out by lot size and a work center's hourly rate
and you get time and cost for the batch.

The second half is the interesting one: released work plans are turned into a
**finite-capacity production schedule**. Jobs compete for the same machines, so
something has to decide who goes first. The app assigns each job a target date,
sequences the work, and reports makespan, tardiness, on-time rate and machine
utilisation, with a Gantt chart.

The whole thing — including a real relational database — runs in the browser as
a static WebAssembly bundle. No backend, no API, no server-side storage.

**Thirty-second version:** *"It manages manufacturing routings and turns them
into a machine schedule. The interesting parts are that the scheduling engine is
a pure library verified against brute-force optimal solutions, and that EF Core
and SQLite run entirely client-side in WebAssembly — the whole app is a static
file drop."*

---

## 2. Architecture, and why

### Two projects, one boundary that matters

```
WorkPlanStudio            Blazor WASM app: pages, services, EF Core, persistence
WorkPlanStudio.Scheduling pure .NET library: no Blazor, no EF, no JS, no WASM
```

Everything else is ordinary layering. The one boundary worth talking about is
the second project.

**Why:** the data layer physically cannot run outside a browser — SQLite is
relinked into `dotnet.native.wasm` and the database is stored through JS interop.
If the algorithm lived next to that code, every test of the interesting logic
would need the `wasm-tools` toolchain and a browser.

**What it bought:** the engine's 108 tests run on a plain .NET host in about two
seconds. That speed is *why* it was affordable to write a test that brute-forces
every one of `n!` job orders — the test that caught the search being 27 % off
optimal. A slow test suite would never have had that test in it.

**How it's enforced:** `ArchitectureTests` reflects over the engine assembly's
references and fails the build if anything from `Microsoft.AspNetCore`,
`Microsoft.EntityFrameworkCore`, `Microsoft.JSInterop` or `SQLitePCLRaw` appears.

> **If asked "isn't an architecture test overkill for a two-project solution?"**
> The comment version of this rule rots the first time someone is in a hurry.
> It's twenty lines and it makes the boundary a build failure instead of a code
> review argument. That said — it's cheap here precisely *because* the solution
> is small. I wouldn't write a bespoke reflection test for a fifty-project
> solution; I'd reach for something like NetArchTest.

### Interfaces: there are three, and each has a reason

This is a common place to get caught, so be precise:

- **`IProductionScheduleService`** — the Scheduling page depends on it so bUnit
  component tests can substitute a fake and render the page with no database and
  no engine run. Used by real tests, not hypothetically.
- **`IDatabaseStore`** — separates *where the database file is kept*
  (`localStorage`) from the rest of `BrowserDatabase` (schema versioning,
  seeding, write-back). The tests pass an in-memory implementation; this is what
  made the data layer testable at all.
- **`IScheduler`** — the weakest of the three. One implementation. It exists as
  a seam between the search and the placement policy, because `LocalSearch` needs
  to re-dispatch candidate orders without knowing how they're placed.

`WorkPlanService` and `WorkCenterService` have **no** interfaces. Nothing needs
to fake them, so they're plain classes.

> **If asked "why does `IScheduler` exist with one implementation?"**
> Say it straight: *"It's the one I'd argue about. It's a real seam — the search
> calls it and shouldn't know how placement works — but if a second scheduler
> never arrives, collapsing it would be fair. I left it because the search
> genuinely depends on the indirection, not because I expect a second one."*
> Do not defend it as "for extensibility". That's the answer they're fishing for.

### What is deliberately absent

No repository wrapper over EF (`DbContext` *is* a unit of work and a repository),
no MediatR, no CQRS, no AutoMapper, no event bus. The app is small enough that
each of those adds indirection without solving a measured problem.

---

## 3. The parts worth talking about

### 3.1 Search quality is a test, not a claim

**This is the strongest thing in the project. Lead with it.**

The engine searches the space of job priority orders: dispatch a candidate order
into a schedule, score it, keep it if it beats the incumbent. The original
implementation used **adjacent swaps** with first-improvement acceptance.

The tell was in the diagnostics: it used **7–16 of its 2000-neighbour budget**
before stalling. A search that never approaches its budget isn't searching.

On instances small enough to enumerate all `n!` orders, the true optimum is
computable — so schedule quality can be *asserted*. Over 20 random 8-job
instances:

| | mean gap to optimum | worst | solved exactly |
| --- | ---: | ---: | ---: |
| dispatch rule alone | 72.5 % | 127.5 % | 0 / 20 |
| adjacent swap | 27.3 % | 62.8 % | 0 / 20 |
| **insertion (or-opt)** | **0.2 %** | **3.0 %** | **19 / 20** |

**Why the neighbourhood was the whole problem:** an adjacent swap moves a job one
position per improving step. A job that belongs ten places earlier is only
reachable if all ten intermediate positions *also* improve the objective. On a
tardiness objective they generally don't — moving an urgent job halfway forward
delays several others without yet fixing the one that matters — so the descent
hits a local optimum almost immediately. Insertion removes the job and re-inserts
it anywhere in one move.

Note what did **not** matter: restart count and acceptance strategy barely moved
the number. The neighbourhood did.

`OptimalityTests` now brute-forces the optimum and asserts the engine reaches it,
so a future regression fails the build instead of quietly producing worse
schedules.

> **If asked "why not simulated annealing / tabu / a proper metaheuristic?"**
> *"Because I measured first. Steepest descent over the right neighbourhood is
> already 0.2 % off optimal on instances of this size — annealing would buy
> almost nothing and cost a temperature schedule I'd have to tune and defend. If
> instances grew to hundreds of jobs I'd revisit it, and I'd want a better lower
> bound than brute force to know whether it helped."*

> **If asked "how do you know it generalises past 8 jobs?"**
> Concede it directly: *"I don't. Eight jobs is where exhaustive enumeration is
> still cheap. Past that I only know the engine beats the rule order, not how far
> from optimal it is. Getting a real answer means an LP/CP lower bound — that's
> the honest gap."*

### 3.2 Six dispatch rules produce four schedules

The dispatch rule and the target-date rule are not independent, and this
surprises people — which makes it good interview material.

With all jobs released at *t* = 0 and *P* = total processing time:

| Target rule | Sets | Therefore |
| --- | --- | --- |
| **TWK** | `due = f · P` | strictly increasing in *P*, so **EDD ≡ SPT**; `CR = due/P = f` is constant, so **CR ≡ FIFO** |
| **SLK** | `due = P + s` | **EDD ≡ SPT**; `CR = (P+s)/P` decreases in *P*, so **CR ≡ LPT** |
| **CON** | `due = c` | all targets equal, so **EDD ≡ FIFO**; `CR = c/P` decreases in *P*, so **CR ≡ LPT** |
| **NOP** | `due = t · n` | keyed on operation count, the only rule that decouples all six |

So on the default targets, six rules produce **four** distinct schedules.

Three options were on the table: delete the redundant rules (wrong — they're only
redundant under *particular* target rules), say nothing (the status quo, and
quietly misleading), or report it. The app reports it: the page tells you which
other rules would give this exact order.

Critically it's computed **from the orders themselves**, not from a hard-coded
table of the identities above. A table would be faster and would drift the first
time a rule's key formula changed.

> **If asked "isn't that just a UI nicety?"**
> *"It's a correctness statement about the parameter surface. Before, changing a
> dropdown could return a byte-identical schedule with no explanation — a user
> reasonably concludes the control is broken. And the equivalences are pinned by
> tests, so the mathematics is documented executably instead of in a comment
> that rots."*

### 3.3 Determinism, and why it's not free

Same seed → bit-for-bit identical schedule, on the desktop, in CI and in the
browser. Two decisions make that true:

- **All time is integer seconds.** Floating-point summation *order* can differ
  between the browser's WASM runtime and the desktop CI runtime. `decimal`
  minutes convert to seconds exactly once, at the mapper, with explicit banker's
  rounding.
- **A hand-rolled PRNG.** `System.Random`'s algorithm is explicitly not stable
  across .NET versions, so a seed wouldn't reproduce after a runtime upgrade. The
  engine uses a fixed-constant xorshift64\*, pinned by a golden-value test.

> **If asked "isn't writing your own PRNG a smell?"**
> *"Normally yes. Here the requirement is reproducibility across runtimes and
> .NET versions, and the framework explicitly doesn't promise that. It's twenty
> lines with documented constants and a golden-value test. The alternative was
> taking a dependency on a third-party PRNG for twenty lines of code."*

### 3.4 Feasible by construction

The search perturbs the job *priority order* and re-dispatches, rather than
moving placed operations around. Every candidate it evaluates is therefore a
valid schedule — no repair step, no infeasible intermediate state.

This costs search power: only schedules reachable by reordering jobs are
explored. It buys a guarantee that's otherwise hard to get. That trade is worth
naming out loud, because it's the kind of thing an interviewer will probe.

### 3.4a Orders own their routing

Ask "what happens if someone edits the routing after the job is on the floor?"
of most CRUD scheduling demos and the honest answer is "it silently changes".

A **work plan** here is master data. A **production order** is a quantity of a
part by a date, and releasing it captures the routing as an immutable snapshot.
The scheduler reads the snapshot, never the live plan.

Demonstrable in about fifteen seconds: note the makespan, open a released work
plan, raise an operation's run time from 0.8 to 66 minutes, save, regenerate —
the schedule is unchanged at 52.2 h.

It also fixed something that had been quietly broken: `DueDateRule.Explicit`
existed in the engine but was **hidden from the UI**, because nothing in the app
could supply a customer due date. Orders supply one, so it is now the default.

> **If asked "why a serialized blob instead of copied rows?"**
> *"It is never queried, only replayed. Copied rows would sit there looking
> editable, and the whole point is that they are not. The blob carries a format
> version so an old snapshot is recognised rather than mis-read."*

### 3.5 EF Core + SQLite in the browser

The headline technical curiosity. The native SQLite engine is relinked into the
app's `dotnet.native.wasm` at build time via the `wasm-tools` workload. On
startup the app reads a base64 SQLite file from `localStorage` into the browser's
virtual file system; after every change it writes back.

**The bug worth telling** — see §5 — is more interesting than the feature.

---

## 4. Alternatives and trade-offs

| Decision | Alternative | Why this way |
| --- | --- | --- |
| Pure engine library | One project | Data layer can't run outside a browser; splitting made the fast test suite — and therefore the optimality test — possible |
| Integer seconds | `double` / `TimeSpan` | FP summation order differs across runtimes; determinism becomes untestable |
| Custom PRNG | `System.Random` | Algorithm not contractually stable across .NET versions |
| `EnsureCreated` + schema version | EF migrations | Migrations preserve data across schema change; here every database is sample data or one user's scratch copy. No production instance to upgrade |
| `localStorage` | IndexedDB | Async and unbounded, but the snapshot is tens of KB and this keeps persistence to ~30 lines. Real cap: a few MB |
| Hand-written CSS | Bootstrap / Tailwind | One look, ~460 lines covers it. The repo *shipped* 8.4 MB of Bootstrap using none of its classes — deleting it was pure win |
| Forward scheduling only | Backward pass from due dates | Under shared finite capacity, two jobs scheduled backwards contend for the same slots and can produce infeasible plans |
| Insertion neighbourhood | Adjacent swap / annealing | Measured: 27.3 % → 0.2 % gap. Annealing would add tuning for near-zero gain at this size |
| No backend | ASP.NET Core API | The "no server" property is the project's distinctive claim. See §7 |

---

## 5. The most interesting bug

**Tell this one.** It's a better signal than any feature, because it's about
verification rather than construction.

The README claimed data survives a page reload. It did not. Create a work
center, reload, and it's gone — replaced by sample data, with no error anywhere.

**Cause:** EF Core opens SQLite in **WAL mode**. A committed transaction lives in
a `workplan.db-wal` side-car file until SQLite checkpoints it. The persistence
code snapshotted only `workplan.db` — which stayed a bare 4 KB header with no
tables. On the next load, `EnsureCreated` found no schema and re-seeded. Every
write was silently discarded.

**Why it hid so well:** the app looked perfect during a session. Every test
passed. The failure only appears across a reload boundary, which no test crossed.

**The fix** is a one-line decision — take SQLite out of WAL mode, so the whole
committed database lives in the single file the snapshot copies. WAL exists to
let readers run concurrently with a writer; this database has one connection on
one thread, so nothing is given up. *(The version on `main` reaches the same
place by checkpointing the WAL before each snapshot — same insight, different
lever.)*

**The lesson to state:** the regression guard is now a test that decodes the
stored blob, opens it as a database, and asserts the rows are there. The stored
blob went from 4096 bytes to 36864. A test that says "the save succeeded" would
still pass on the broken version — the assertion has to cross the same boundary
the user does.

Two smaller ones found in the same pass, both worth a sentence:

- **`HasDefaultValue` defeated a check constraint.** With a default configured,
  EF treats the CLR default as "unset" and omits the column — so `Capacity = 0`
  silently became `1` instead of violating `CK_WorkCenter_Capacity`.
- **A strict CSP broke the app entirely.** Blazor emits a fingerprinted inline
  importmap whose content changes every build, so a hash goes stale immediately
  and a static host can't mint a nonce. Found by testing the CSP against the
  *published* artifact instead of the dev server.

---

## 6. Remaining weaknesses

Name these before you're asked. Volunteering a real weakness reads as
confidence; being caught hiding one reads as the opposite.

1. **Optimality is only verified to 8 jobs.** Past that, the engine is a descent
   with no known bound. A real answer needs an LP/CP relaxation for a lower
   bound.
2. **The calendar is periodic and uniform.** Availability windows repeat over a
   fixed period, so "08:00–16:00 every day" works but "closed on public holidays"
   and "Friday is a half day" do not. Exceptions to the pattern would need a real
   calendar model.
3. **No gap back-filling.** A job's operations are placed in sequence without
   inserting later work into earlier idle windows. Fixing it would break the
   "the order determines the schedule" property the search depends on — that's
   why it hasn't been.
4. **`localStorage` caps the database at a few MB.** Fine for the demo; the app
   warns when a write doesn't fit rather than losing it silently.
5. **Single user, no auth.** No server, so nothing to authenticate against. This
   is a scope decision, not an oversight — but it means the project shows nothing
   about authn/authz.
6. **The dispatch-rule selector is now more teaching device than lever.** A good
   optimiser makes the starting rule largely irrelevant; different rules converge
   on the same schedule unless you switch the search off. Correct behaviour, but
   worth being upfront that it reduces what the control demonstrates.
7. **CI runs on one OS.** Determinism is claimed across runtimes but only
   verified on Linux CI plus local Windows.

---

## 7. The "where's the backend?" question

You will get this. It is the single most likely challenge, because most .NET
roles are server-side.

**Don't be defensive, and don't oversell the WASM angle as if it replaces a
backend.** The honest answer has three parts:

1. **It's a deliberate constraint, and it produced real engineering.** Running EF
   Core against SQLite compiled to WebAssembly, and persisting it correctly
   across reloads, is a harder problem than a CRUD API over Postgres — as the WAL
   bug demonstrates.
2. **It costs specific things, and I know which.** No authn/authz, no API design,
   no migrations story, no horizontal scaling, no server-side observability.
3. **I know what adding one would look like.** An ASP.NET Core Web API serving
   the schedule, Postgres with real EF migrations replacing the schema-version
   reset, a Dockerfile with a `/health` endpoint, and `WebApplicationFactory`
   integration tests. That would also mean rewriting ADR 0001 — the pure-library
   boundary is only worth its cost *because* the alternative host can't run
   tests.

If the role is backend-heavy, say plainly that you'd build a separate
server-side project rather than bolt an API onto this one, because mixing the two
would destroy the property that makes this one distinctive.

---

## 8. Likely questions, with answers

**"Walk me through what happens when I click Generate."**
The page projects its form into an immutable `SchedulingParameters`. The service
loads released work plans and active work centers, and `ScheduleMapper` converts
them into engine inputs — this is the only place `decimal` minutes become integer
seconds, with banker's rounding. `DueDateAssigner` gives each job a target.
`PriorityOrdering` turns the dispatch rule into an initial job sequence. The
engine then runs a descent from that order and from each seeded shuffle,
re-dispatching every candidate through `DispatchScheduler`, keeping the best by
penalty. The result is mapped back into Gantt rows and KPI cards.

**"Why is the scheduling engine a separate project?"** → §2.

**"How do you know the schedules are any good?"** → §3.1. Lead with the table.

**"What's your test strategy?"**
Four layers, weighted by where the risk is. 135 engine tests on a plain runner —
unit, property-based invariants via CsCheck, brute-force optimality, and
adversarial cases chosen to break a plausible-but-wrong implementation. 65 web
tests including a real SQLite database on a normal host, EF→domain mapping,
accessibility semantics and localization parity. bUnit component tests for the page with a faked service.
Playwright end-to-end through a real browser. The pyramid is deliberate: the
bottom layer is fast enough that expensive tests like exhaustive enumeration are
affordable.

**"Why test localization?"**
Because a missing `.resx` key doesn't throw. `IStringLocalizer` renders the key
name, so the symptom is a raw `Sched_KpiMakespan` in the German UI that no test
catches. Parity, placeholder counts and "every key used in a component exists"
are now assertions.

**"How does the app stay responsive during a long search?"**
It doesn't, fully — WebAssembly is single-threaded and the engine blocks the UI
thread. Two mitigations: the page yields to the event loop before starting so the
"Generating…" state actually paints, and the search budget is clamped so a large
value can't freeze the tab. The real fix is a web worker, which Blazor WASM
doesn't make easy. That's a known limitation, not a solved problem.

**"What would you do differently starting over?"**
Write the optimality test first. It's the test that found the search was 27 % off,
and everything else — feasibility, determinism, invariants — passed happily on
the weak version. I built the search, then discovered how weak it was; the right
order is to make quality measurable before optimising it.

**"What's the worst code in the project?"**
Pick one and mean it. Candidates: `IScheduler` as a single-implementation
interface (§2); the schema-version-reset-instead-of-migrations decision, which is
right for a demo and wrong for anything real; the Gantt label threshold, which is
a percentage-of-makespan heuristic standing in for actual text measurement.

**"How long does a schedule take?"**
About 10 ms for 8 jobs, 533 ms for 100, with the default 8 restarts. The budget
parameter now actually binds — before the neighbourhood change the search never
approached it, so the knob was decorative.

---

## 9. Numbers worth remembering

| | |
| --- | --- |
| Engine library | ~1 340 lines |
| Blazor app | ~3 950 lines |
| Tests | 212 tests (135 engine / 65 web / 12 E2E) |
| Engine coverage | 96.4 % line, 89.1 % branch |
| Search gap to optimum | 0.2 % mean, 19/20 solved exactly |
| Schedule runtime | ~10 ms at 8 jobs, ~533 ms at 100 |
| Dead Bootstrap removed | 8.4 MB, 44 files |
| Stored DB before/after the WAL fix | 4 096 → 36 864 bytes |

---

## 10. Before the interview

- Open the live demo and change the flow factor from 3.0 to 0.5. Watch the jobs
  go late. That's your demo — one control, visible consequence.
- Switch the dispatch rule to Critical Ratio and point at the line that says it's
  the same order as FIFO. Explain why in one sentence.
- Re-read §5. If you tell one story, tell that one.
- Know your weakest answer (§6) before someone finds it for you.
