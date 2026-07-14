# WorkPlan Studio — završni production hardening izveštaj

Datum revizije: 2026-07-13
Grana: `codex/production-platform`

## Executive summary

WorkPlan Studio sada ima dva namerno odvojena režima. GitHub Pages ostaje samostalni Blazor WebAssembly portfolio demo sa SQLite bazom u browseru. Hosted režim je ASP.NET Core aplikacija sa PostgreSQL/SQLite providerom, Identity cookie autentifikacijom, CSRF zaštitom, owner-scoped API-jem, stvarnim `ProductionOrder` modelom, kalendarima kapaciteta i perzistiranim background schedule runovima.

Najvažnija promena nije broj projekata već pomeranje granice poverenja: browser više nije authority za production podatke ili AI credential. Scheduler ostaje čista deterministička biblioteka; API validira identitet i ownership, persistence sloj čuva routing snapshot naloga, a worker izvršava ograničene schedule runove. Statički demo nije uklonjen jer je koristan kao javni, zero-infrastructure prikaz.

Finalna klasifikacija mora ostati konzervativna: ovo je jak portfolio dokaz za mid-level razgovor, ali nije dokaz kompletnog MES/APS ili regulisanog HA proizvoda. Worker sada ima multi-replica-safe DB lease, TOTP MFA/recovery i runtime/load smoke; nema confirmed-email/password-reset delivery, reprezentativni soak/penetration test ni operativni HA dokaz.

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
3. Background run je prvo zapisan u bazu, zatim stavljen u bounded channel. Startup ponovo queue-uje `Queued`/`Running` zapise. Ovo pruža crash recovery za jedan proces, ne distributed exactly-once semantiku.
4. Production auth koristi HttpOnly, Secure, SameSite=Strict Identity cookie, antiforgery header i owner filter na svakom business query-ju.
5. Data Protection key ring je na posebnom Compose volume-u, pa auth cookie ne postaje nečitljiv posle zamene kontejnera.
6. `/health/live` proverava proces bez baze; `/api/health/ready` proverava konekciju i pending migrations. Orchestrator zato ne restartuje zdrav proces samo zbog privremenog DB prekida.
7. Production AI poziv ide preko authenticated, rate-limited server proxy-ja sa fiksnim HTTPS endpointom, server-side secretom, bounded facts payloadom i 20 s timeoutom. AI samo preformuliše objašnjenje.
8. Statički BYOK režim je zadržan samo kao jasno označen offline demo trade-off.
9. Native SQLite bundle je podignut na patched graph; CI više nema advisory suppression i `dotnet list ... --vulnerable --include-transitive` je čist.
10. Docker runtime je non-root, read-only, bez Linux capabilities i sa `no-new-privileges`; writable su samo `/tmp` i Data Protection volume.

## Build i test evidence

Konačne brojke se održavaju u [TESTING.md](TESTING.md). Obavezni gate uključuje pojedinačne test projekte, Release build, format, dependency audit, migration script generation, scenario ceilings, Compose config, browser E2E i authenticated API smoke. Poznati `WASM0001` potiče od varargs exporta u SQLite native biblioteci; aplikacija ne poziva te configuration overloads, a real Chromium CRUD/reload E2E je obavezna regresija.

## Rizici

### Critical

- Nema poznatog otvorenog critical blockera u deklarisanom portfolio scope-u.

### Important

- DB-backed atomski claim, lease/heartbeat i persisted cancellation sada sprečavaju duplu obradu između API replika; tenant fairness i poseban worker autoscaling model nisu implementirani.
- TOTP MFA i jednokratni recovery kodovi su završeni; confirmed-email/password-reset delivery i help-desk recovery ostaju operativni identity gap.
- Data Protection volume mora biti backupovan i za višestruke hostove zaštićen certificate/KMS mehanizmom.
- CI sada ima 100-request/20-concurrency HTTP load smoke, security-header probe i realan container/WASM runtime smoke, ali nema reprezentativni soak/load, nezavisni penetration test ni ljudski NVDA/VoiceOver audit.

### Accepted portfolio limitations

- GitHub Pages demonstrira offline storage i nije multi-user production deployment.
- Heuristika nema optimality proof; performance scenario nije production capacity SLA.
- AI provider je opcioni narrator i nikada scheduling authority.
- Compose runbook je single-host referentni deployment, ne HA platforma.

### Production roadmap

- Tenant queue quotas, idempotency key i outbox za side effects.
- OIDC/enterprise identity ili kompletan confirmed-account/password-reset delivery tok.
- Managed PostgreSQL, managed secrets/KMS, central logs/traces i alert rules.
- Load/soak test sa realnim routing distributions; OR-Tools/CP-SAT evaluacija tek kada heuristika ne zadovolji merljivi SLA.

## Procena posle izmena

| Kategorija | Ocena | Dokaz / ograničenje |
| --- | ---: | --- |
| Correctness | 9/10 | Domain/boundary/property/API/E2E i mali exhaustive oracle; nema opšti optimality dokaz |
| Reliability | 9/10 | DB lease/heartbeat/recovery, cross-replica cancellation, readiness i backup |
| Architecture | 9/10 | čist scheduler, shared contracts/domain, provider migrations; svesno bez pattern inflation-a |
| Maintainability | 8/10 | jasne granice i ADR; API endpoint fajlovi ostaju ručno mapirani |
| Testability | 9/10 | 99 engine + 54 web + 11 API + 10 browser testova, real SQLite i Chromium |
| Security | 8.5/10 | Identity/CSRF/ownership/rate limits/TOTP/recovery codes; bez nezavisnog pen testa |
| Accessibility | 8.5/10 | keyboard/modal/lang/mobile + Chrome AX-tree/label/contrast audit; bez ljudskog NVDA/VoiceOver testa |
| UX | 8.5/10 | status tiles, empty/live states, localized statuses, accessible progress/cancel i account-security UI |
| Dokumentacija | 9/10 | architecture/security/production/AI/ADR/intervju dokumenti |
| GitHub prezentacija | 9/10 | problem, dijagram, live demo, evidence i limitations odmah vidljivi |
| Interview readiness | 9/10 | dokazive tehničke priče i iskren AI disclosure |
| Production readiness | 8.5/10 | multi-replica-safe worker i runtime/load/security smoke; nije HA/regulated/soak dokaz |

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
