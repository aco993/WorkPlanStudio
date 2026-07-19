# WorkPlan Studio — demo skripta

## Priprema

```bash
dotnet restore WorkPlanStudio.slnx
dotnet build WorkPlanStudio.slnx -c Release --no-restore
dotnet run --project src/WorkPlanStudio/WorkPlanStudio.csproj -c Release --no-build
```

Za javni demo koristi čist browser context za očekivani seed. Za hosted demo pokreni `docker compose` sa lokalnim `.env`, pripremi test korisnika i test podatke; nikada ne koristi pravi production credential. Drži otvorene `ProductionOrderEndpoints.cs`, `ScheduleWorker.cs`, `BrowserDatabase.cs` i po jedan API/E2E test.

## Demo od 2 minuta

0:00–0:20 — Otvori dashboard: “Ovo je .NET 10 aplikacija za routinge, production orders i finite-capacity scheduling; javni demo radi offline, a isti klijent ima authenticated hosted režim.”

0:20–0:50 — Work Plans: otvori plan, pokaži operacije, lot summary i released status. “Validacija nije samo HTML; servis i SQLite ponavljaju invarijante.”

0:50–1:30 — Scheduling: promijeni flow factor 3.0 → 0.5 i generiši. Pokaži late pills/Gantt i rule-based explanation. “Heuristika je deterministička; AI ne odlučuje.”

1:30–1:50 — Promijeni DE i pokaži `html lang` kroz DevTools samo ako je brzo.

1:50–2:00 — Zaključi: “Najjači dokaz nije screenshot nego šest slojeva: 102 engine, 55 web/data, 12 API, 2 prava PostgreSQL, 10 offline-browser i 3 authenticated production-browser testa.”

## Demo od 5 minuta

Dodaj na 2-minutni flow:

- 0:00–0:40: problem, tehnologije i dual-mode granica;
- 0:40–1:40: u hosted režimu prijava → work center → routing → production order sa due date; u offline rezervi pokaži Draft plan i hard reload;
- 1:40–2:10: pokušaj LotSize `-1`; pokaži lokalizovanu poruku i da UI nije crashovao;
- 2:10–3:20: schedule factor/rule/seed, Gantt i explanation;
- 3:20–4:00: Work Center modal; Escape i focus return; capacity polje;
- 4:00–4:35: otvori `ScheduleMapper` diagnostic result i `BrowserDatabase` WAL checkpoint;
- 4:35–5:00: pokaži DB lease/MFA/SMTP-reset dokaz i priznaj preostalo: nema neograničenog job-shop optimality dokaza, nezavisnog pen testa, ljudskog NVDA/VoiceOver potpisa ni target-environment HA vežbe.

## Demo od 10 minuta

1. Minut 0–1: poslovni problem, architecture diagram i hosted/offline trade-off.
2. Minut 1–3: login, owner-scoped CRUD, invalid input, released/inactive center pravilo i real relational constraints.
3. Minut 3–4: hard reload; objasni WAL bug i zašto desktop test nije bio dovoljan.
4. Minut 4–6: scheduler: target assignment, dispatch slots, multi-start, local search, penalty, determinism.
5. Minut 6–7: `ProductionOrder` routing snapshot, calendar/setup i persisted schedule run progress/cancel.
6. Minut 7–8: storage recovery koncept: corrupt/schema payload ostaje za export; reset nije migracija.
7. Minut 8–9: test pyramid i performance rezultat; pokaži komande, ne samo brojeve u README-u.
8. Minut 9–10: AI disclosure, sigurnost i tri roadmap odluke.

## Architecture walkthrough redoslijed

```text
Pages/*.razor
→ ServerSession + typed client services
→ Identity/CSRF/owner-scoped API endpoints
→ ProductionDbContext
→ PostgreSQL

Offline fallback:
Pages/*.razor → local services → BrowserDatabase → SQLite WASM/localStorage

ProductionSchedule.razor
→ ScheduleRunService
→ persisted ScheduleRunQueue / ScheduleWorker
→ ProductionOrder snapshot + calendars/setup
→ WorkPlanStudio.Scheduling
→ ScheduleResult + ScheduleExplainer
→ optional authenticated server narrator
```

Na svakoj strelici reci šta ulazi kao untrusted i koji test provjerava granicu.

## Rezervni demo

- Screenshot par: `docs/schedule-ontime.png` / `docs/schedule-late.png`.
- Pokreni samo scheduling tests; zatim web tests ako WASM build već postoji.
- Pokaži E2E kod za hard reload i posljednji zabilježeni rezultat.
- Ako GitHub Pages koristi stariji main, reci da je hardening na PR grani; ne predstavljaj deploy kao novu verziju prije merge-a.
