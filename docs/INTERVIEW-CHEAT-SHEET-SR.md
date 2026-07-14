# WorkPlan Studio — intervju cheat sheet

## Pet rečenica koje moraju biti tačne

- Scheduler je deterministička heuristika, nije optimal solver.
- AI je opcioni narrator, nije dio scheduling odluke.
- Offline SQLite/localStorage je eksplicitna demo persistence; hosted režim koristi Identity API i PostgreSQL.
- Released routing je potpuno scheduleovan ili potpuno odbijen sa structured reason codeom.
- AI je intenzivno korišćen; odgovornost je u provjeri, debugovanju, testovima, integraciji i odlukama, ne u tvrdnji da je sve ručno napisano.

## Brojevi za 2026-07-13

- Baseline commit: `0e1e3c514f35d8479dfe1bdc51905d31de8729b3`.
- Scheduling tests: 93 passed.
- Web/data/component tests: 54 passed.
- Production API integration tests: 5 passed.
- Playwright E2E: 10 passed, uključujući save→hard reload, confirmed reset i mobile drawer.
- Current engine coverage: 452/469 linija (96.38%) i 201/235 grana (85.53%); nove calendar/setup/horizon grane povećale su denominator.
- Scenario runner: 25/100/250 jobs; 32.9/275.2/601.0 ms i 0.57/42.73/436.57 MB allocations na finalnom runu.
- Central budget: 1–64 starts, 0–20.000 local steps.
- Work-center capacity: 1–64.
- Production AI timeout: 20 s.

## Najbolje tri tehničke priče

1. Data loss → real browser reproduction → dispose nije dovoljan → WAL console evidence → checkpoint → hard-reload E2E.
2. Silent partial routing → all-or-nothing preparation result → localized link do problematičnog plana.
3. Corrupt/schema storage → nema catch-and-reseed → typed recovery, export, confirmed reset, ADR.
4. `ProductionOrder` → immutable routing snapshot → master-data izmena ne menja istorijski nalog.
5. Browser startup → `ScopedInSingletonException` → ispravan DI lifetime + real Chromium regresija.

## Rečenice koje ne koristiti

- “Enterprise/HA production-ready.”
- “Optimal scheduling.”
- “Zero vulnerabilities.”
- “Complete Clean Architecture.”
- “AI-powered scheduler.”
- “Sve sam ručno napisao.”
- “Skalira” bez vezivanja za konkretan scenario i ograničenje.

## Brza mapa koda

- `Data/BrowserDatabase.cs`: WAL snapshot i recovery boundary.
- `WorkPlanStudio.Api/Program.cs`: auth, CSRF, health, rate limit i composition root.
- `WorkPlanStudio.Api/Endpoints/`: owner-scoped HTTP granice.
- `WorkPlanStudio.Api/Scheduling/`: persisted run queue/worker.
- `WorkPlanStudio.Domain/ProductionOrder.cs`: order i routing snapshot.
- `WorkPlanStudio.Persistence/ProductionDbContext.cs`: server relational boundary/audit.
- `Data/AppDbContext.cs`: relational last defense.
- `Validation/`: business validators.
- `Services/ScheduleMapper.cs`: decimal→seconds i all-or-nothing mapping.
- `WorkPlanStudio.Scheduling/SchedulingEngine.cs`: orchestration.
- `Core/DispatchScheduler.cs`: feasible list scheduling.
- `Core/LocalSearch.cs`: bounded first-improvement.
- `tests/.../BrowserDatabaseTests.cs`: real-SQLite failure evidence.
- `tests/.../ScheduleE2ETests.cs`: browser reload/accessibility evidence.
- ADR 0006/0007: browser persistence i ProductionOrder snapshot odluke.

## Ako se ne zna odgovor

Reći: “To nisam dokazao u ovom projektu. Trenutni dokaz je X, rizik je Y, a provjerio bih ga metodom Z.” To je bolji mid-level signal od improvizovane senior tvrdnje.
