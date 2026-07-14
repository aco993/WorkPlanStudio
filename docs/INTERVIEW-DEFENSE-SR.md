# WorkPlan Studio — intervju odbrana

> Aktuelni hosted/offline arhitektonski pregled, ocene i production rizici nalaze se u [PRODUCTION-HARDENING-REPORT-SR.md](PRODUCTION-HARDENING-REPORT-SR.md). Pitanja 1–32 ispod detaljno brane originalni offline demo i scheduler; pitanja 33–40 pokrivaju novu production platformu.

Ovaj dokument nije skripta za preuveličavanje projekta. Kandidat treba da pokaže kod i test koji podržavaju odgovor, a ograničenje da prizna prije nego što ga intervjuista pronađe.

## Predstavljanje od 30 sekundi

WorkPlan Studio je .NET 10 aplikacija za proizvodne routinge, naloge i konačno-kapacitivno raspoređivanje. Javni Blazor WASM demo radi offline sa SQLite bazom u browseru, dok hosted režim koristi ASP.NET Core, Identity, owner-scoped API i PostgreSQL. Scheduler je čista deterministička heuristika, a AI je samo opcioni narrator. Najjači engineering dokaz su eksplicitne trust boundaries i regresije za data integrity, autentifikaciju, tenant izolaciju, kalendare i setup vremena.

## Architecture walkthrough

1. Blazor stranice prikazuju i prikupljaju lokalizovan input.
2. `WorkPlanService` i `WorkCenterService` normalizuju i validiraju mutation na servisnoj granici, vraćaju mali typed result i rade sa EF contextom.
3. `AppDbContext` ponavlja tvrde invarijante kroz required kolone, check constraints, foreign keys i unique indekse.
4. `BrowserDatabase` učitava versioned Base64 payload, provjerava korupciju/schema verziju i prije snapshot-a checkpointuje WAL.
5. `ProductionScheduleService` učitava released planove i centre, a `ScheduleMapper` svaki plan potpuno prihvata ili potpuno odbija sa strukturiranim razlogom.
6. `WorkPlanStudio.Scheduling` nema Blazor/EF/JS zavisnosti. Dodjeljuje ciljeve, pravi dispatch redoslijed, radi seeded multi-start i bounded local search, pa vraća feasible rezultat i determinističko objašnjenje.
7. Opcioni AI dobija samo strukturirane činjenice i može samo preformulisati objašnjenje; kvar pada nazad na rule-based narrator.

## Scheduling algoritam

List scheduler prolazi kroz job priority order. Za svaki operation bira slot radnog mjesta koji se najranije oslobađa, a start je maksimum prethodnog completion-a posla i slobodnog vremena slota. Tako su precedence i capacity zadovoljeni konstrukcijom. Multi-start pravi determinističke seeded permutacije, a adjacent-swap first-improvement local search zadržava samo strogo bolji penalty. Kompleksnost jednog dispatch-a je približno `O(operations × capacity)`; ukupno se množi brojem startova i local-search evaluacija. Rezultat nije dokazano globalni optimum.

## Persistence trade-off

SQLite radi u WASM in-memory file sistemu, a `localStorage` čuva versioned Base64 snapshot. EF koristi WAL, zato snapshot prvo radi `PRAGMA wal_checkpoint(TRUNCATE)`, zatvara context, pa čita glavni fajl. Reload provjerava Base64, minimalnu veličinu/header, `PRAGMA quick_check` i očekivanu tabelu. Schema mismatch nije migracija: stari payload ostaje netaknut, može se eksportovati, a reset zahtijeva potvrdu. To je pošten demo kompromis; produkcija bi tražila migracije, backup/rollback, server storage i support policy.

## AI-assisted razvoj — iskren odgovor

AI alati su intenzivno korišćeni za početnu generaciju i kasniji review/hardening. Ne tvrdim da je sav kod ručno napisan niti izmišljam procenat AI-generisanih linija. Moja odgovornost je specifikacija, provjera, debugging, testiranje, integracija i finalne odluke. Konkretan primjer reviewa je persistence bug: browser E2E je pokazao data loss, konzola je otkrila WAL, a prihvaćeno rješenje ima real-SQLite i save→hard-reload dokaz. Ako ne mogu objasniti accepted code i njegove trade-offe, ne smatram ga svojim završenim radom.

## Pitanja, odgovori i follow-up

### 1. Zašto SQLite u browseru?

- Kratko: da demonstrira pravi relational data layer u statičkoj aplikaciji bez backenda.
- Detaljno: omogućava EF modele, FK, unique/check constraints i LINQ, ali uvodi WASM build, WAL snapshot, quota i migration ograničenja. Za pravi multi-user proizvod izabrao bih backend bazu.
- Follow-up: Kada bi IndexedDB bio bolji? Kada je relational model manje vrijedan od jednostavnijeg native browser storagea.

### 2. Koji je najteži bug koji si našao?

- Kratko: save je izgledao uspješno, ali reload je vraćao seed podatke.
- Detaljno: prvo je snapshot rađen prije dispose-a; zatim je WASM E2E pokazao da ni dispose nije dovoljan jer EF koristi WAL. Snapshot glavnog fajla bez checkpointa nema najnovije stranice. Rješenje checkpointuje WAL, zatvara context i tek onda enkoduje fajl.
- Follow-up: Zašto desktop test prvobitno nije bio dovoljan? Browser VFS/WAL ponašanje mora se dokazati u realnom WASM runtimeu.

### 3. Zašto ne koristiš EF migrations?

- Kratko: current browser demo nema bezbjedan, testiran historical-schema migration policy.
- Detaljno: polovična migracija bi bila opasnija od eksplicitnog ograničenja. ADR 0006 bira export + confirmed reset i nikada silent overwrite.
- Follow-up: Šta bi prava migracija zahtijevala? Version chain, backup, transactional rollback, old-schema fixtures i failure recovery.

### 4. Kako razlikuješ corrupt storage od fresh installa?

- Kratko: `null` payload je fresh; postojeći nevalidan payload je typed recovery failure.
- Detaljno: invalid Base64, truncated data, wrong SQLite, schema mismatch i read/write failure imaju različite enum razloge i testove.
- Follow-up: Zašto ne catch-all pa seed? Zato što bi to pretvorilo kvar u tihi gubitak podataka.

### 5. Kako sprječavaš partial routing?

- Kratko: plan je potpuno mapiran ili potpuno odbijen.
- Detaljno: mapper akumulira structured issues sa planom, operacijom i reason codeom. Inactive/missing center, duplicate operation ili duration overflow odbijaju cijeli released plan.
- Follow-up: Da li se validni planovi ipak raspoređuju? Da, ali UI jasno kaže da rezultat nije kompletan i linkuje odbijene planove.

### 6. Gdje postoji validacija?

- Kratko: UI hints, servisna business granica, DB constraints i scheduling boundary.
- Detaljno: HTML min/max je UX; validator je autoritativan za svakog caller-a; SQLite je zadnja zaštita; mapper/`SchedulingContext` ne vjeruju persistence podacima.
- Follow-up: Zašto nema posebnih command DTO-a? Trenutni mali UI nema više input kanala; dodatni modeli nisu opravdali mapping trošak. To bih promijenio za API/import.

### 7. Koja status pravila postoje?

- Kratko: enum mora biti validan; archived ne ide direktno u released; released routing mora biti kompletan i koristiti aktivne centre.
- Detaljno: archived se vraća u Draft prije ponovne release provjere, što čini namjeru eksplicitnom.
- Follow-up: Da li je to univerzalno proizvodno pravilo? Ne; to je dokumentovana demo odluka koju bi product owner potvrdio.

### 8. Kako rješavaš concurrency?

- Kratko: demo je single-user/single-origin i nema pravi distributed concurrency model.
- Detaljno: typed NotFound/Conflict sprječavaju lažni uspjeh za stale update i uniqueness, ali nema row versiona ni merge workflowa.
- Follow-up: Šta bi dodao na serveru? Optimistic concurrency token, 409 response i conflict UX.

### 9. Zašto typed results, a ne exceptions?

- Kratko: validation/conflict/not-found su očekivani ishodi, ne neočekivani kvarovi.
- Detaljno: mali `ApplicationResult<T>` omogućava UI-u da ne navigira nakon neuspjelog save-a. Neočekivani kvar se loguje i ide u lokalni panel/ErrorBoundary.
- Follow-up: Zašto nije generički Result framework? Tri servisa ne opravdavaju novi framework i složene monade.

### 10. Kako štitiš scheduling budget?

- Kratko: centralni hard limits, checked arithmetic i cancellation.
- Detaljno: 1–64 multi-start, 0–20.000 local steps, validirani due parametri i display day. Brojčani budget čuva determinism; token se provjerava u engine/local search/dispatch petljama.
- Follow-up: Zašto ne time limit? Elapsed-time cutoff može dati različit rezultat na različitom hardveru.

### 11. Zašto scheduler nije async?

- Kratko: CPU-bound je; lažni `Task.Run` u WASM-u ne rješava UI thread.
- Detaljno: sada je bounded i cancellable, a Worker se uvodi tek nakon browser profiliranja reprezentativnih problema.
- Follow-up: Kada bi Worker bio obavezan? Kada UI long-task profil i input distribucija pokažu vidljivo blokiranje.

### 12. Da li je rezultat optimalan?

- Kratko: ne; deterministička je heuristika.
- Detaljno: čuva feasible schedule i nikad ne vraća gori penalty od pure dispatch kandidata, ali nema optimality proof ili lower-bound gap.
- Follow-up: Kada OR-Tools? Za calendars, alternatives, setup matrices, global bounds i hard delivery constraints.

### 13. Kako dokazuješ determinism?

- Kratko: fixed PRNG, integer seconds, stable ordering i ponovljeni signature testovi.
- Detaljno: nema `System.Random`, wall-clock cutoffa ni dictionary-order zavisnosti. Property/unit/E2E i performance runner ponavljaju isti input/seed.
- Follow-up: Može li floating-point penalty uticati? Candidate generation ostaje determinističan; poređenje koristi isti runtime put, ali cross-platform floating semantics ostaju tema za stroži golden test.

### 14. Zašto integer seconds?

- Kratko: eliminišu akumulaciju decimal/floating greške u engineu.
- Detaljno: decimal manufacturing minutes se jednom checked konvertuju banker's roundingom u mapperu.
- Follow-up: Šta sa sub-second procesima? Model ih zaokružuje; druga rezolucija bi bila domain odluka.

### 15. Kako radi parallel capacity?

- Kratko: svako radno mjesto ima 1–64 identična slota.
- Detaljno: dispatcher vodi `freeAt` sat za svaki slot i bira najraniji; tie ide nižem indeksu radi determinisma.
- Follow-up: Da li slotovi modeluju različite mašine? Ne; identični su. Heterogene alternative zahtijevaju bogatiji model.

### 16. Koji test je najvredniji?

- Kratko: save→hard reload u Chromiumu.
- Detaljno: presijeca UI, servis, EF, SQLite WASM, WAL, JS interop i localStorage; upravo je našao kvar koji desktop integration test nije.
- Follow-up: Zašto ipak zadržati niže testove? Brže lokalizuju invalid Base64, constraints i typed outcomes.

### 17. Da li mockuješ EF?

- Kratko: ne za relational semantiku.
- Detaljno: `BrowserDatabaseTests` koriste file-backed SQLite. Hand-built entiteti se koriste samo za pure mapper granicu.
- Follow-up: Zašto ne InMemory provider? Ne provjerava SQLite constraints, collation, FK ili SQL ponašanje.

### 18. Kako rješavaš N+1?

- Kratko: usage counts se dobijaju jednim grouped projection queryjem.
- Detaljno: prethodni UI je radio query po centru; sada servis vraća dictionary iz jednog SQL group-by-a.
- Follow-up: Zašto nema cache? Sedam redova i jednostavan query ne opravdavaju invalidation složenost.

### 19. Kako UI ostaje otključan nakon greške?

- Kratko: busy state se resetuje u `finally`.
- Detaljno: schedule, AI i save akcije razlikuju očekivane rezultate; bUnit test namjerno baca exception i provjerava enabled dugme i safe poruku.
- Follow-up: Šta hvata ErrorBoundary? Neočekivane render/event greške koje nisu smisleno obrađene na lokalnoj granici.

### 20. Šta je urađeno za accessibility?

- Kratko: dinamički `html lang`, pravi dialog semantics, lokalizovan close, Escape, focus entry/return i vidljiv focus.
- Detaljno: bUnit provjerava ARIA, a Playwright pravi real keyboard/focus roundtrip.
- Follow-up: Šta nedostaje? Potpun audit sa screen readerom i focus trap za kompleksnije modale.

### 21. Kako štitiš BYOK ključ?

- Kratko: hosted režim uopšte ne daje ključ browseru; localStorage BYOK postoji samo u eksplicitnom offline demo režimu.
- Detaljno: production proxy koristi operator-configured HTTPS endpoint, server-side secret, 20 s timeout i rate limit. Demo upozorenje jasno kaže da localStorage nije vault.
- Follow-up: Da li XSS može pročitati ključ? Da; zato localStorage nije secret vault i produkcija mora koristiti backend.

### 22. Zašto AI nije scheduler?

- Kratko: scheduling odluke moraju biti deterministične i testabilne.
- Detaljno: AI samo preformuliše structured facts; rule-based explanation je uvijek dostupna i provider kvar ne utiče na schedule.
- Follow-up: Šta se šalje provideru? Sažetak, bottleneck, late-job facts i preporuka, ne baza.

### 23. Kako je rešen SQLite advisory?

- Kratko: uveden je direktan patched `SQLitePCLRaw.bundle_e_sqlite3` graph i suppression je uklonjen.
- Detaljno: Release WASM link i real Chromium CRUD/reload regresije proveravaju kompatibilnost; CI pokreće transitive vulnerability audit.
- Follow-up: Preostali rizik? `WASM0001` upozorava na varargs configuration exporte koje aplikacija ne poziva; svaka buduća SQLite promena mora ponoviti browser gate.

### 24. Zašto je uveden ProductionOrder?

- Kratko: `WorkPlan` je master routing, a izvršni nalog mora imati quantity, release/due, priority, status i stabilnu routing reviziju.
- Detaljno: nalog čuva immutable routing snapshot, pa kasnija promena master plana ne menja već release-ovan posao niti auditabilnost schedule rezultata.
- Follow-up: Zašto snapshot umesto samo FK-a? FK čuva referencu, ali ne istorijski sadržaj koji je zaista planiran.

### 25. Kako testiraš corrupt storage?

- Kratko: fake storage fault injection + prava SQLite validacija.
- Detaljno: invalid Base64, short payload, non-SQLite, schema mismatch, read/write/quota, export/reset su automatizovani.
- Follow-up: Šta nije automatizovano u browseru? Svaki recovery reason nema poseban Playwright scenario; storage core je integration-testovan, normal reload E2E.

### 26. Zašto schema version 3?

- Kratko: capacity i novi constraints mijenjaju model.
- Detaljno: version bump namjerno vodi stare demo baze u export/reset umjesto silent reseed-a.
- Follow-up: Da li export garantuje import? Ne; trenutno je backup artefakt za forenziku/manual recovery, ne migration format.

### 27. Šta performance brojevi stvarno znače?

- Kratko: reproducibilan relative signal, ne production SLA.
- Detaljno: finalni 250 jobs/2.000 ops/5.000 local steps run je bio 601.0 ms i 436.57 MB allocations na četiri-logical-CPU Windows hostu; determinism je potvrđen. To je ceiling regression, ne SLA.
- Follow-up: Zašto allocations tako rastu? Svaki neighbor klonira order i ponovo materializuje schedule/evaluation; pooling/incremental evaluation je optimization roadmap.

### 28. Koji refactoring nisi uradio?

- Kratko: nisam dodao repository/MediatR/CQRS niti fragmentirao Razor bez mjerljive koristi.
- Detaljno: ostao je direktan EF service sloj i jedan vrijedan interface za bUnit seam.
- Follow-up: Kada bi repository imao smisla? Više storage implementacija ili domain persistence port koji EF detalji stvarno krše.

### 29. Da li je aplikacija production-ready?

- Kratko: hosted režim je deployable single-host production baseline, ali nije HA/regulated production dokaz.
- Detaljno: postoje Identity, CSRF, owner scoping, PostgreSQL migracije, backup/restore, Data Protection key ring, health probes i telemetry export. Nedostaju distributed queue lease, MFA/account-recovery operativa, load/soak i penetration test.
- Follow-up: Zašto ne kažeš samo production-ready? Zato što spremnost zavisi od threat modela, SLO-a, operativnog tima i deployment topologije, ne samo od feature liste.

### 30. Kako CI štiti kvalitet?

- Kratko: odvojeni engine, web/WASM i Playwright workflowi, warnings-as-errors i NuGet audit.
- Detaljno: deploy je test-gated, permissions su minimalne po workflowu, Dependabot prati NuGet/actions.
- Follow-up: Slabost? Deploy trenutno gateuje engine, dok PR workflowi nose web/E2E; stroži release environment bi zahtijevao sve required checks.

### 31. Zašto je `Schedule.razor` još velik?

- Kratko: veličina je poznata, ali ekstrakcija nije urađena bez jasnog state/test dobitka.
- Detaljno: page drži povezane parametre, rezultat, assistant i modal state. Sljedeći opravdan rez je parameter form ili result presentation ako dobiju zaseban behavior/reuse.
- Follow-up: Šta ne bi radio? Deset trivijalnih child komponenti koje samo prosljeđuju parametre.

### 32. Šta bi sljedeće uradio?

- Kratko: distributed run claim i kompletan identity recovery tok, tek zatim skaliranje optimizacije.
- Detaljno: trenutna granica je single API replica. DB lease/idempotency rešavaju correctness pri scale-out-u; OIDC ili confirmed account/MFA rešavaju operativni identity gap. Solver menjam tek uz merljiv SLA i realne podatke.
- Follow-up: Zašto ne CP-SAT odmah? Zato što sadašnja heuristika ispunjava demonstracioni scenario uz nižu složenost i determinističko objašnjenje.

### 33. Kako server sprečava tenant data leak?

- Kratko: svaki business query filtrira po authenticated owner id-u; klijentski id nije authority.
- Detaljno: Identity cookie formira principal, endpoint zahteva policy, a EF query kombinuje resource id i owner id. Integration test registruje dva korisnika i dokazuje da drugi ne vidi prvi work center.
- Follow-up: Da li je to dovoljno za multi-tenant SaaS? Za jači model dodao bih global query filter, tenant-aware DbContext i DB-level RLS kao defense in depth.

### 34. Zašto cookie auth i CSRF, a ne JWT u localStorage-u?

- Kratko: browser aplikaciji ne treba JS-readable bearer token; HttpOnly cookie smanjuje posledice XSS-a, a CSRF token štiti mutacije.
- Detaljno: cookie je `Secure`, `SameSite=Strict`, `HttpOnly`; API redirect pretvara u 401/403, a mutating endpoint traži `X-CSRF-TOKEN`.
- Follow-up: Kada JWT ima smisla? Za non-browser clients ili odvojeni authorization server, uz bezbedan token lifecycle.

### 35. Kako background run preživljava restart?

- Kratko: stanje i input su u bazi; startup vraća `Queued` i prethodno `Running` runove u bounded queue.
- Detaljno: progress/result/failure su persisted, a worker poštuje cancellation. To je at-least-once recovery u jednom procesu, ne distributed exactly-once.
- Follow-up: Kako scale-out? Atomic DB claim sa lease/heartbeat ili durable broker, plus idempotent result write.

### 36. Zašto odvojene SQLite i PostgreSQL migrations assemblies?

- Kratko: provider DDL i annotation nisu potpuno prenosivi.
- Detaljno: isti model koristi dve migrations istorije; CI generiše oba SQL script-a. Time se ne pretvaramo da SQLite migration automatski dokazuje PostgreSQL deployment.
- Follow-up: Rizik? Model drift; zato obe skripte moraju biti deo gate-a.

### 37. Šta tačno proveravaju health probe-ovi?

- Kratko: liveness samo potvrđuje da proces odgovara; readiness proverava DB konekciju i da nema pending migration-a.
- Detaljno: DB outage ne sme izazvati restart loop procesa, ali instanca bez baze ne sme primati saobraćaj.
- Follow-up: Da li readiness treba autentifikaciju? Tipično ne unutar zaštićene orchestration mreže; odgovor ne sme izlagati secrets.

### 38. Kako su Data Protection ključevi rešeni?

- Kratko: Compose montira persistent key-ring volume i aplikacija koristi stabilan application name.
- Detaljno: bez toga novi container ne može dekriptovati stare auth cookies. Za više hostova key ring mora biti shared i zaštićen certificate/KMS mehanizmom.
- Follow-up: Da li volume sam šifruje ključeve? Ne; to je eksplicitni preostali operativni zadatak.

### 39. Kako production AI razlikuje od demo BYOK-a?

- Kratko: production secret nikada ne ide u browser; authenticated server proxy prima samo bounded computed facts.
- Detaljno: endpoint/model su operator konfiguracija, HTTPS je obavezan, poziv ima 20 s timeout i poseban rate limit, a rule-based narrator je fallback.
- Follow-up: Može li prompt injection promeniti schedule? Ne, model nema scheduling authority niti write alat; ipak output ostaje untrusted presentation text.

### 40. Koji bug je browser dijagnostika našla u ovom hardeningu?

- Kratko: aplikacija je ostala na loaderu zbog singleton `BackendState` koji je zavisio od scoped `HttpClient`-a.
- Detaljno: browser console je pokazao `ScopedInSingletonException`; lifetime je promenjen u scoped. Zatim je dodat `JsonException` fallback jer static host za API putanju može vratiti HTML SPA fallback.
- Follow-up: Kako je sprečena regresija? Release build i real Chromium E2E; offline startup više ne zavisi od validnog API JSON-a.

## Slabosti koje kandidat treba sam da prizna

- Browser storage nije migracija, backup ni multi-device sync.
- Scheduler radi na UI threadu i heuristički je; nema optimality gap.
- Hosted queue je single-replica; nema distributed claim/lease.
- AI ključ u localStorageu je kompromis isključivo za eksplicitni offline BYOK demo.
- Hosted identity nema završen MFA/email-confirmation/password-delivery operativni tok.
- Nema screen-reader audit-a, samo automatizovane semantic/keyboard provjere.
- Performance rezultat je sa jedne mašine i generated data seta.

## Mogući live-coding zadaci

1. Dodati novi `SchedulePreparationErrorCode` za zero-duration operation, lokalizovati ga i napisati mapper test bez parsiranja poruke.
2. Dodati optimistic concurrency polje i demonstrirati conflict result na real-SQLite testu (objasniti zašto browser demo nema pravi cross-user konflikt).
3. Optimizovati local search da ponovo koristi buffer za candidate order, zadržati determinism i pokazati scenario allocations prije/poslije.

## Rezervni plan ako demo ne radi

1. Ne skrivati kvar; pokazati posljednji CI/komandni dokaz i tačan commit.
2. Otvoriti `docs/schedule-ontime.png` i `docs/schedule-late.png` za vizuelni flow.
3. Pokrenuti čisti scheduling test projekat, zatim `BrowserDatabaseTests` ili izolovani E2E.
4. Proći kroz `BrowserDatabase.PersistAsync`, `ScheduleMapper.BuildInput` i jedan regresioni test.
5. Jasno reći šta nije upravo demonstrirano uživo.
