# WorkPlan Studio — samoevaluacija zasnovana na dokazima

Datum: 2026-07-19
Grana: `codex/production-platform`

## Zaključak

Repository je sada jak, deployable portfolio production baseline, ali nije pošteno
označiti ga kao „100/100 production sistem“. Broj **100/100** je opravdan samo za
deklarisani scheduling-engine coverage: 508/508 linija i 245/245 grana. Ukupna
production assurance ocena mora ostati niža jer ljudski screen-reader audit,
nezavisni penetration test i stvarna multi-host/failover vežba ne mogu nastati
samim pisanjem koda u repository-ju.

## Izvršeni dokazi

| Dokaz | Rezultat 2026-07-19 |
| --- | --- |
| Testovi | 185 passed, 0 failed, 0 skipped u eksplicitno orkestriranim slojevima |
| Engine coverage | 508/508 linija i 245/245 grana — 100% / 100% |
| Release build | uspešan, 0 errors; ostaju dva dokumentovana `WASM0001` warning bloka |
| Format i dokumentacija | `dotnet format` čist; 36 Markdown fajlova bez nepostojećih lokalnih linkova |
| Paketi | nema prijavljenog direct/transitive advisory-ja i nema dostupnog top-level update-a u trenutku revizije |
| Baze | oba migration skripta generisana; PostgreSQL 18 migracije/model i lease fencing 2/2 |
| Browser | offline Chromium 10/10; authenticated production Chromium 3/3 |
| Container | PostgreSQL/API healthy; Npgsql readiness; non-root, read-only, `cap_drop=ALL`, `no-new-privileges` |
| Remote PR gate | svih 10 obaveznih checkova zeleno; CodeQL, real PostgreSQL, production container/E2E i ZAP izvršeni |
| HTTP smoke | 60/60 odgovora 200 na 10 RPS; 0 grešaka; latency percentili zabeleženi |
| Account recovery | Mailpit SMTP delivery potvrđen; reset uspeo; ponovna upotreba tokena odbijena |
| Log privacy | bootstrap startup potvrđuje postojanje naloga bez zapisivanja email adrese; integracioni test hvata regresiju |
| Performance | 25/100/250-job scenariji deterministični i ispod CI time/allocation plafona |

GitHub Actions sada dodatno imaju immutable Action/image pinove, fail-closed NuGet
audit, CodeQL `security-extended`, stvarni PostgreSQL servis, authenticated
production E2E i puni Pages release gate. Njihov status je remote dokaz i mora se
čitati sa aktuelnog PR-a; lokalni rezultat nije zamena za GitHub runner rezultat.

## Ocene

| Kategorija | Ocena | Razlog |
| --- | ---: | --- |
| Correctness | 9.5/10 | property/integration/browser testovi i 100% engine coverage; heuristika nije opšti optimalni solver |
| Reliability | 9/10 | fenced lease/heartbeat/takeover, readiness, backup/runbook; nema izvedene target HA vežbe |
| Architecture | 9/10 | čist scheduler, jasni Domain/Contracts/Persistence/API slojevi i ADR granice |
| Maintainability | 8.5/10 | centralni paketi, format, docs-link gate i jasni runbook-ovi; širok scope i ručno mapirani endpointi nose trošak |
| Testability | 9.5/10 | šest slojeva i realne SQLite/PostgreSQL/Chromium granice; nema dugog reprezentativnog data soak-a |
| Security | 9/10 | auth/CSRF/ownership/rate limit/MFA/reset, audit, headeri, CodeQL i supply-chain pinovi; bez nezavisnog pen testa |
| Accessibility | 8.5/10 | keyboard/modal/lang/mobile i production DOM/AX semantika automatizovani; bez ljudskog NVDA/VoiceOver potpisa |
| UX | 8.5/10 | statusi, lokalizovane greške, offline/server stanje, progress/cancel i account tokovi; funkcionalni UI nije formalno usability-testiran ni dizajnerski finalizovan |
| Dokumentacija | 9.5/10 | aktivni dokumenti usklađeni i linkovi automatski provereni; operativne evidencije moraju nastajati po deploymentu |
| GitHub/CI | 9.5/10 | svih deset obaveznih PR checkova zeleno, CodeQL/artifacts/digest pinovi/full deploy gate i aktivna zaštita `main` grane; spoljne usluge ipak ostaju operativna zavisnost |
| Production readiness | 8.5/10 | stvarni Docker/PostgreSQL/SMTP baseline i replica-safe claim; nema multi-zone infrastrukture, failover/RPO/RTO ni regulatornog potpisa |

## Šta i dalje nije završivo samo kodom

1. **NVDA/VoiceOver audit** zahteva imenovanog čoveka, konkretan uređaj/browser i
   beleženje subjektivnog govornog izlaza. Automatizovani DOM/AX test nije isto.
2. **Nezavisni penetration test** mora izvesti druga strana nad dogovorenim targetom
   i pravilima angažmana. CodeQL, dependency audit i ZAP su dopunski dokazi.
3. **HA/regulated dokaz** zahteva najmanje dve stvarne replike/zone, managed bazu i
   key store, kontrolisani kill/failover, backup restore/PITR, monitoring/on-call i
   potpis odgovornih osoba. Compose je namerno single-host baseline.
4. **Dugi soak** je sada rate-controlled i zakazan, ali prolaz se sme tvrditi tek
   nakon pregleda kompletnog četvoročasovnog artefakta. Kratak smoke nije soak.
5. **Globalni optimum** nije svojstvo trenutnog algoritma. Exact oracle dokazuje
   najbolji dispatch order samo do devet poslova u tom ograničenom modelu; za opšti
   job-shop treba formalni CP-SAT/MILP model, bounds i definisan solution-quality SLA.

Spoljni potpisi pripadaju u
[EXTERNAL-ASSURANCE-CHECKLIST.md](EXTERNAL-ASSURANCE-CHECKLIST.md); prazna polja su
namerni dokaz da nisu izmišljena.
