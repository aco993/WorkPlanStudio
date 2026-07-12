# WorkPlan Studio — production hardening rezultati

Datum: 2026-07-12

Polazni commit: `0e1e3c514f35d8479dfe1bdc51905d31de8729b3`
Grana: `codex/hardening-mid-level-readiness`

## 1. Baseline

| Stavka | Potvrđeno početno stanje |
| --- | --- |
| Repository | `HEAD == origin/main == 0e1e3c5`, clean, 0 ahead/behind |
| SDK/runtime | SDK 10.0.301 uz pinned 10.0.300 feature band; runtime 10.0.9 |
| Restore/build/format | prolaze; Release build ima samo dva poznata `WASM0001` warning group-a, 0 errors |
| Scheduling testovi | 83 passed, 0 failed/skipped |
| Web testovi | 26 passed, 0 failed/skipped |
| Playwright | 6 passed, 0 failed/skipped |
| Coverage | 373/381 linija (97.90%), 142/155 grana (91.61%) |
| Security | high transitive `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, GHSA-2m69-gcr7-jv3q |
| Runtime | startup/schedule/EN-DE/mobile rade; keyboard rezultat nije bio dovoljno pouzdan za prolaz |
| Novi P0 nalaz | create/save izgleda uspješno, ali browser reload gubi podatke |

Kompletan audit trag i threat model su u [HARDENING-BASELINE.md](HARDENING-BASELINE.md).

## 2. Implementirane izmjene

| Problem/root cause | Rješenje i glavni fajlovi | Neizabrana alternativa | Dokaz | Preostali rizik |
| --- | --- | --- | --- | --- |
| Save→reload data loss: snapshot prije flush/dispose; zatim potvrđen WAL root cause | `BrowserDatabase` radi `wal_checkpoint(TRUNCATE)`, zatvara context, tek onda snapshot; typed write failure | samo `try/catch` ili samo dispose nisu dovoljni | real SQLite reload test + Playwright hard reload | `localStorage` quota i dalje ograničava veličinu |
| Schema mismatch/corrupt payload je crashovao ili silent reseedovao | typed recovery za Base64/header/truncated/SQLite/schema/read/write; export + two-step reset; ADR 0006 | nepotpuna “migration” | fault-injection/SQLite testovi i confirmed-reset E2E | nema cross-version importa/migracija |
| Negativan lot je prolazio UI i rušio scheduling | centralni validators, typed mutation results, localized UI, SQLite check constraints/unique indexes | oslanjanje na HTML min/max | business, real-SQLite, bUnit i invalid-input E2E | EF entiteti ostaju edit modeli; poseban DTO tek za novi input kanal |
| Inactive/missing operation se tiho izbacivao | `SchedulePreparationResult`, whole-plan rejection, stable reason code i link do plana | partial schedule sa tekstualnim warningom | mapper multi-plan tests + bUnit diagnostics | validni planovi se i dalje scheduleuju uz jasno “incomplete” upozorenje |
| Capacity hardcoded na 1 | validated 1–64 persistence/UI/domain field i DB constraint | poseban machine-instance model | mapper/validator/DB test | slotovi su identični, ne heterogene alternative |
| Neograničen search i overflow | central 64/20.000 limits, checked seconds/due/end arithmetic, cooperative cancellation | elapsed-time cutoff bi narušio determinism | boundary/extreme/cancellation tests | CPU i dalje radi na UI threadu |
| Busy state ostaje zaključan nakon exceptiona | targeted catches, `finally`, localized safe panels, top-level `ErrorBoundary`, logging | global catch-and-ignore | bUnit throwing service + invalid E2E | nema production telemetry backend |
| N+1 usage count | jedan grouped projection query | cache za sedam redova | real-SQLite CRUD/usage test | bez značajnog rizika |
| Modal/lang accessibility | dialog/ARIA/name, localized close, Escape, focus entry/return, visible focus, dynamic `html lang` | samo vizuelna modal stilizacija | bUnit ARIA + Playwright keyboard/lang/mobile | nije izvršen pun screen-reader audit/focus trap audit |
| BYOK endpoint/timeout | HTTPS policy, localhost dev izuzetak, bez user-info/query/fragment, 15 s timeout, safe fallback | browser prihvata bilo koji URL | endpoint/transport/cancellation testovi | localStorage je čitljiv same-origin JS-u |
| Lažni Explicit due-date feature | sakriven u app UI; engine capability zadržan; ADR 0007 definiše `ProductionOrder` roadmap | polovičan order model | bUnit provjerava da opcija nije izložena | nema customer-order due dates |
| Neodbranjive performance tvrdnje | deterministic scenario runner + dokumentovana complexity/memory granica | microbenchmark kao SLA | 25/100/250-job reproducibilni run | generated data i jedna mašina nisu production capacity test |

## 3. Završni test evidence

| Komanda/provjera | Rezultat |
| --- | --- |
| `dotnet restore WorkPlanStudio.slnx --disable-parallel` | passed |
| `dotnet build WorkPlanStudio.slnx -c Release --no-restore -m:1 -nodeReuse:false` | passed; 2 poznata `WASM0001` warning group-a, 0 errors |
| `dotnet format WorkPlanStudio.slnx --verify-no-changes --no-restore` | passed |
| `git diff --check` | passed |
| scheduling test project | 90 passed, 0 failed, 0 skipped |
| web/data/component test project | 54 passed, 0 failed, 0 skipped |
| Playwright E2E | 10 passed, 0 failed, 0 skipped; 2m19s |
| engine coverage | 411/424 lines = 96.93%; 160/183 branches = 87.43% |
| package outdated | nema dostupnih direct updates |
| package vulnerable | isti high transitive SQLite advisory u app/web-test graphu; documented `SEC-001` |
| secret pattern scan | nema match-eva |
| Release publish | passed nakon čistog build-server/dev-server stanja; `index.html` postoji, 73 `.wasm` fajla |
| performance runner | small 2.0 ms/0.32 MB; medium 85.1 ms/11.17 MB; large 394.6 ms/243.43 MB; svi repeated signatures jednaki |
| GitHub PR CI | engine/coverage passed (28 s); web/component passed (59 s); Playwright passed (1m59s) |

Dijagnostički failure-i nisu sakriveni: prvi full E2E je timeoutovao zbog reload recovery-ja; izolacija je pokazala `no such table: WorkCenters` i WAL root cause, nakon čega finalnih 10 prolazi. Jedan paralelni Release build i prvi publish su timeoutovali u lokalnom WASM toolchainu; single-node build i clean-state publish prolaze. To je environment/tooling contention, ali komande za stabilan run su dokumentovane.

## 4. Unresolved risks

### Critical

- Nema poznatog neriješenog critical functional blockera u deklarisanom portfolio scopeu.

### Important

- `SEC-001`: high transitive SQLite advisory nema patched package u trenutnom podržanom graphu; suppression ostaje usko dokumentovan.
- Scheduling radi na main UI threadu; maksimalni dozvoljeni budget može biti neprijatan na slabom uređaju.
- Nema punog assistive-technology/screen-reader audita.

### Accepted portfolio limitation

- Local browser demo storage, bez pravih migracija, cloud backupa, sync-a ili multi-user concurrencyja.
- BYOK key u localStorageu nije secret-vault rješenje.
- Released `WorkPlan` je demonstration job, ne `ProductionOrder`.
- Heuristika nema optimality proof ni production SLA.

### Production roadmap

- Backend auth/API/storage, versioned migrations i backup/restore.
- `ProductionOrder`, routing revision snapshot, calendars i order due/release/priority.
- Browser profiling pa Worker, ili background service; solver evaluacija (CP-SAT/MILP) za bogatije constraints.
- Supported patched SQLite chain, telemetry, CSP/security headers i private vulnerability reporting.

## 5. Arhitektonska procjena

| Kategorija | Ocjena / 10 | Dokaz |
| --- | ---: | --- |
| Correctness | 9.0 | checked arithmetic, all-or-nothing routing, constraints, 154 tests |
| Reliability | 8.5 | WAL reload, typed recovery, quota/write outcome, ErrorBoundary; nema server durabilityja |
| Architecture | 8.5 | enforced pure core i mali application boundary bez pattern inflationa |
| Maintainability | 8.0 | central limits/validators/ADRs; `Schedule.razor` je i dalje veći stateful page |
| Testability | 9.0 | property, real SQLite, fault injection, bUnit, 10 browser scenarija |
| Security | 7.0 | honest BYOK/endpoint/timeout/audit posture; high advisory i client secret limitation ostaju |
| Accessibility | 7.5 | semantics/focus/Escape/lang/mobile dokaz; nema screen-reader audita |
| UX | 8.0 | localized actionable errors/recovery; recovery export nema re-import workflow |
| Documentation | 9.0 | architecture/security/performance/ADRs/intervju/demo i honest disclosure |
| GitHub presentation | 8.5 | 30–60 s README tok, screenshots, CI, limitations; live demo će prikazati hardening tek nakon merge/deploya |
| Interview readiness | 9.0 | executable evidence, 32 pitanja, demo varijante i priznate slabosti |
| Production readiness | 5.5 | namjerno static single-user demo; nema auth/backend/migrations/ops modela |

## 6. Hiring-manager procjena

- Otvorio bih CV nakon 30 sekundi: da. README brzo objašnjava problem, live demo, arhitekturu, testove i ograničenja.
- Poziv na intervju: da, za mid-level .NET/full-stack razgovor; ne bih ga samostalno koristio kao senior dokaz.
- Najbolji utisak: WAL bug od reprodukcije do E2E dokaza; pure deterministic scheduler sa property testovima; pošten scope/security/AI disclosure.
- Tri sumnje: AI-heavy autorstvo zahtijeva live objašnjenje; browser persistence je neobičan demo kompromis; nema stvarnog production-order/calendar modela.
- Kandidata može oboriti: tvrdnja da je scheduler optimalan/production-ready, nemogućnost objašnjenja WAL/checkpointa ili prihvatanje AI koda bez razumijevanja.
- Objektivna procjena: projekat sada prolazi kao clean mid-level portfolio signal. Ne prolazi kao production-ready proizvod ili samostalan senior signal.
- Sigurnost procjene: 0.90, jer su build/test/runtime/publish i sva tri PR CI checka izvršeni; nezavisni reviewer feedback još nije dostupan.

Predlog GitHub descriptiona: `Bilingual Blazor WASM portfolio app for manufacturing routings with EF Core/SQLite in-browser persistence and a deterministic finite-capacity scheduler.`

Predlog topics: `blazor-webassembly`, `dotnet`, `entity-framework-core`, `sqlite`, `manufacturing`, `scheduling`, `property-based-testing`, `playwright`, `portfolio`.

## 7. Intervju i AI paket

- Potpuna odbrana, 32 pitanja, detailed/follow-up odgovori, slabosti i live-coding zadaci: [INTERVIEW-DEFENSE-SR.md](INTERVIEW-DEFENSE-SR.md).
- Brzi podsjetnik: [INTERVIEW-CHEAT-SHEET-SR.md](INTERVIEW-CHEAT-SHEET-SR.md).
- Demo od 2/5/10 minuta i fallback: [DEMO-SCRIPT-SR.md](DEMO-SCRIPT-SR.md).

clean mid-level
