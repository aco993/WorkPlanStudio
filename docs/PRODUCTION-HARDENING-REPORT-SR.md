# WorkPlan Studio — završni production hardening izveštaj

Datum revizije: 2026-07-19
Grana: `codex/production-platform`

## Executive summary

WorkPlan Studio sada ima dva namerno odvojena režima. GitHub Pages ostaje samostalni Blazor WebAssembly portfolio demo sa SQLite bazom u browseru. Hosted režim je ASP.NET Core aplikacija sa PostgreSQL/SQLite providerom, Identity cookie autentifikacijom, CSRF zaštitom, owner-scoped API-jem, stvarnim `ProductionOrder` modelom, kalendarima kapaciteta i perzistiranim background schedule runovima.

Najvažnija promena nije broj projekata već pomeranje granice poverenja: browser više nije authority za production podatke ili AI credential. Scheduler ostaje čista deterministička biblioteka; API validira identitet i ownership, persistence sloj čuva routing snapshot naloga, a worker izvršava ograničene schedule runove. Statički demo nije uklonjen jer je koristan kao javni, zero-infrastructure prikaz.

Finalna klasifikacija mora ostati konzervativna: ovo je jak portfolio dokaz za mid-level razgovor, ali nije dokaz kompletnog MES/APS ili regulisanog HA proizvoda. Worker ima multi-replica-safe DB lease, TOTP MFA/recovery, SMTP password reset i runtime/soak/ZAP automatizaciju; ljudski screen-reader potpis, nezavisni penetration test i target-environment HA vežba ostaju spoljne assurance aktivnosti.

## Tehnologije i struktura

- C# / .NET 10, Blazor WebAssembly i ASP.NET Core hosted API.
- EF Core sa SQLite providerom za test/local režim i Npgsql/PostgreSQL za production režim.
- ASP.NET Core Identity, antiforgery, authorization policies, rate limiting i Data Protection.
- OpenTelemetry traces/metrics, health probes, Docker/Compose i PowerShell backup/restore.
- xUnit v3, bUnit, CsCheck, `WebApplicationFactory` i Playwright.
- `WorkPlanStudio.Scheduling`: dependency-free scheduling domain.
- `WorkPlanStudio.Domain`: production entiteti i invariants.
- `WorkPlanStudio.Contracts`: transport contracts bez EF/UI zavisnosti.
- `WorkPlanStudio.Persistence`: DbContext, Identity schema, audit i SQLite migrations.
- `WorkPlanStudio.PostgresMigrations`: provider-specific PostgreSQL migrations.
- `WorkPlanStudio.Api`: composition root, auth/API endpointi, worker i server AI proxy.
- `WorkPlanStudio`: Blazor klijent i eksplicitni offline demo fallback.

## Arhitektura i tipičan tok

```text
User action
→ Razor event + input validation
→ typed client service + CSRF/session handling
→ authenticated API endpoint + owner policy
→ EF Core transaction / domain validation / audit
→ PostgreSQL
→ DTO response
→ component state refresh
```

Scheduling tok:

```text
ProductionOrder routing snapshot + capacity calendars
→ POST schedule run
→ persisted queued run
→ bounded in-process worker
→ pure Scheduling engine
→ persisted progress/result/failure
→ polling UI + optional server-side narration
```

Offline demo tok ostaje `Razor → application services → BrowserDatabase → SQLite WASM/localStorage`. `BackendState` bira server samo kada readiness vraća validan JSON `ready`; HTTP, timeout, unsupported content ili JSON parse greška u `Auto` režimu bezbedno padaju na lokalni demo.

## Najvažnije implementacione odluke

1. `ProductionOrder` čuva quantity, release/due, priority/status i immutable routing snapshot. Scheduler ne čita promenljiv master routing tokom izvršenja naloga.
2. Availability windows, downtime i sequence-dependent setup ulaze u feasibility, a nedovoljan horizon daje eksplicitnu grešku umesto neograničene pretrage.
3. Background run je prvo zapisan u bazu; lokalni channel je samo wake-up hint. Svaka replika atomski claim-uje DB lease, obnavlja heartbeat i može preuzeti istekao run, dok owner-fenced completion i persisted cancellation sprečavaju stale rezultat.
4. Production auth koristi HttpOnly, Secure, SameSite=Strict Identity cookie, antiforgery header i owner filter na svakom business query-ju.
5. Data Protection key ring je na posebnom Compose volume-u, pa auth cookie ne postaje nečitljiv posle zamene kontejnera.
6. `/health/live` proverava proces bez baze; `/api/health/ready` proverava konekciju i pending migrations. Orchestrator zato ne restartuje zdrav proces samo zbog privremenog DB prekida.
7. Production AI poziv ide preko authenticated, rate-limited server proxy-ja sa fiksnim HTTPS endpointom, server-side secretom, bounded facts payloadom i 20 s timeoutom. AI samo preformuliše objašnjenje.
8. Statički BYOK režim je zadržan samo kao jasno označen offline demo trade-off.
9. Native SQLite bundle je podignut na patched graph; CI više nema advisory suppression i `dotnet list ... --vulnerable --include-transitive` je čist.
10. Docker runtime je non-root, read-only, bez Linux capabilities i sa `no-new-privileges`; writable su samo `/tmp` i Data Protection volume.

## Build i test evidence

Konačne brojke se održavaju u [TESTING.md](TESTING.md). Lokalna revizija je izvršila 184/184 testa bez failure/skip rezultata, Release build, format, dokumentacione linkove, fail-closed dependency audit, oba migration skripta, scenario ceilings, Compose/PostgreSQL health, production security headers/restrictions, offline i authenticated production Chromium, SMTP reset delivery/single-use token i kratak rate-controlled soak. Poznati `WASM0001` potiče od varargs exporta u SQLite native biblioteci; aplikacija ne poziva te configuration overloads, a real Chromium CRUD/reload E2E je obavezna regresija.

## Rizici

### Critical

- Nema poznatog otvorenog critical blockera u deklarisanom portfolio scope-u.

### Important

- DB-backed atomski claim, lease/heartbeat i persisted cancellation sada sprečavaju duplu obradu između API replika; tenant fairness i poseban worker autoscaling model nisu implementirani.
- TOTP MFA, jednokratni recovery kodovi i jednosatni single-use password-reset tok preko konfigurisanog SMTP-a su završeni; help-desk identity proofing ostaje organizaciona procedura.
- Data Protection volume mora biti backupovan i za višestruke hostove zaštićen certificate/KMS mehanizmom.
- CI ima realan container/WASM smoke, SMTP/Mailpit reset dokaz i 100-request load smoke; zaseban workflow daje podesivi soak, petominutni PR dokaz, nedeljni četvoročasovni soak i OWASP ZAP baseline. To i dalje nije nezavisni penetration test ni ljudski NVDA/VoiceOver audit.

### Accepted portfolio limitations

- GitHub Pages demonstrira offline storage i nije multi-user production deployment.
- Heuristika nema neograničeni job-shop optimality proof; bounded exact optimizer dokazuje optimum unutar dispatch-order modela do devet poslova.
- AI provider je opcioni narrator i nikada scheduling authority.
- Compose runbook je single-host referentni deployment, ne HA platforma.

### Production roadmap

- Tenant queue quotas, idempotency key i outbox za side effects.
- OIDC/enterprise identity i formalni help-desk identity-proofing tok.
- Managed PostgreSQL, managed secrets/KMS, central logs/traces i alert rules.
- Reprezentativni routing workload i target-environment capacity test; OR-Tools/CP-SAT evaluacija kada bounded dispatch-order dokaz ne zadovolji merljivi SLA.

## Procena posle izmena

| Kategorija | Ocena | Dokaz / ograničenje |
| --- | ---: | --- |
| Correctness | 9/10 | 100/100 engine coverage, property/API/E2E i bounded exhaustive dispatch-order dokaz; nema opšti job-shop dokaz |
| Reliability | 9/10 | DB lease/heartbeat/recovery, cross-replica cancellation, readiness i backup |
| Architecture | 9/10 | čist scheduler, shared contracts/domain, provider migrations; svesno bez pattern inflation-a |
| Maintainability | 8/10 | jasne granice i ADR; API endpoint fajlovi ostaju ručno mapirani |
| Testability | 9.5/10 | 184 testova u šest slojeva, real SQLite/PostgreSQL, offline i production Chromium |
| Security | 9/10 | Identity/CSRF/ownership/rate limits/TOTP/recovery/SMTP reset, fail-closed audit, digest pins i CodeQL; bez nezavisnog pen testa |
| Accessibility | 8.5/10 | keyboard/modal/lang/mobile + authenticated production semantics/AX evidence; bez ljudskog NVDA/VoiceOver testa |
| UX | 8.5/10 | status/empty/live/progress/cancel/account tokovi; bez formalnog usability testa i završnog dizajnerskog polish-a |
| Dokumentacija | 9.5/10 | provereni lokalni linkovi, architecture/security/production/AI/ADR/intervju dokumenti i nova samoevaluacija |
| GitHub prezentacija | 9/10 | problem, dijagram, live demo, evidence i limitations; konačna ocena zavisi od zelenih novih PR checkova |
| Interview readiness | 9/10 | dokazive tehničke priče i iskren AI disclosure |
| Production readiness | 8.5/10 | real PostgreSQL/container/SMTP evidence i multi-replica-safe worker; nije target-environment HA/regulated potpis |

## Hiring-manager procena

- Posle 30 sekundi: otvorio bih README i zatim CV, jer repository pokazuje poslovni problem, live demo, test evidence i ograničenja bez buzzworda.
- Poziv: da, za mid-level .NET/full-stack intervju; za senior bih tražio iskustvo sa stvarnim operacijama, incidentima i skaliranjem izvan ovog projekta.
- Najbolji utisak: pure/deterministic scheduler sa property testovima; data-integrity priče (WAL i routing snapshot); production trust boundary sa auth/CSRF/ownership.
- Sumnje: veliki AI doprinos zahteva live objašnjenje; DB lease se ne sme prodavati kao kompletna HA platforma; širok scope može sakriti plitko razumevanje ako kandidat ne može pratiti jedan request end-to-end.
- Kandidata može oboriti tvrdnja da je scheduler optimalan, da je projekat enterprise-ready ili da je sav kod ručno napisan.
- Procena: projekat objektivno daje `clean mid-level` portfolio signal ako kandidat može objasniti accepted code i ograničenja. Sigurnost procene: 85%; production operativno iskustvo se ne može dokazati samim repository-jem.

## AI autorstvo — odgovor koji treba koristiti

“AI alati su intenzivno korišćeni i početna implementacija nije sva ručno napisana. Ne izmišljam procenat AI-generisanih linija. Moj doprinos i odgovornost su specifikacija, arhitektonske odluke, reprodukcija grešaka, review, debugging, regresioni testovi, integracija i odluka šta ulazi u finalni kod. Mogu da prođem kroz svaki prihvaćeni kritični tok — od Identity/CSRF requesta do owner-scoped EF query-ja, ili od `ProductionOrder` snapshot-a do scheduling rezultata. AI output koji nisam mogao da objasnim ili dokažem testom nisam smatrao završenim.”

## Final verdict

**clean mid-level**
