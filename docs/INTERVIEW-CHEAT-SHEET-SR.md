# WorkPlan Studio — intervju cheat sheet

## Pet rečenica koje moraju biti tačne

- Scheduler je deterministička heuristika, nije optimal solver.
- AI je opcioni narrator, nije dio scheduling odluke.
- SQLite/localStorage je eksplicitna lokalna demo persistence, nije production migracija ili secret vault.
- Released routing je potpuno scheduleovan ili potpuno odbijen sa structured reason codeom.
- AI je intenzivno korišćen; odgovornost je u provjeri, debugovanju, testovima, integraciji i odlukama, ne u tvrdnji da je sve ručno napisano.

## Brojevi za 2026-07-12

- Baseline commit: `0e1e3c514f35d8479dfe1bdc51905d31de8729b3`.
- Scheduling tests: 90 passed.
- Web/data/component tests: 54 passed.
- Playwright E2E: 10 passed, uključujući save→hard reload, confirmed reset i mobile drawer.
- Current engine coverage: 411/424 linija (96.93%) i 160/183 grane (87.43%); baseline je bio 97.90/91.61% prije novih validation/cancellation grana.
- Scenario runner: 25/100/250 jobs; približno 2.0/85.1/394.6 ms na review mašini.
- Central budget: 1–64 starts, 0–20.000 local steps.
- Work-center capacity: 1–64.
- AI timeout: 15 s.

## Najbolje tri tehničke priče

1. Data loss → real browser reproduction → dispose nije dovoljan → WAL console evidence → checkpoint → hard-reload E2E.
2. Silent partial routing → all-or-nothing preparation result → localized link do problematičnog plana.
3. Corrupt/schema storage → nema catch-and-reseed → typed recovery, export, confirmed reset, ADR.

## Rečenice koje ne koristiti

- “Production-ready.”
- “Optimal scheduling.”
- “Zero vulnerabilities.”
- “Complete Clean Architecture.”
- “AI-powered scheduler.”
- “Sve sam ručno napisao.”
- “Skalira” bez vezivanja za konkretan scenario i ograničenje.

## Brza mapa koda

- `Data/BrowserDatabase.cs`: WAL snapshot i recovery boundary.
- `Data/AppDbContext.cs`: relational last defense.
- `Validation/`: business validators.
- `Services/ScheduleMapper.cs`: decimal→seconds i all-or-nothing mapping.
- `WorkPlanStudio.Scheduling/SchedulingEngine.cs`: orchestration.
- `Core/DispatchScheduler.cs`: feasible list scheduling.
- `Core/LocalSearch.cs`: bounded first-improvement.
- `tests/.../BrowserDatabaseTests.cs`: real-SQLite failure evidence.
- `tests/.../ScheduleE2ETests.cs`: browser reload/accessibility evidence.
- ADR 0006/0007: persistence i ProductionOrder scope.

## Ako se ne zna odgovor

Reći: “To nisam dokazao u ovom projektu. Trenutni dokaz je X, rizik je Y, a provjerio bih ga metodom Z.” To je bolji mid-level signal od improvizovane senior tvrdnje.
