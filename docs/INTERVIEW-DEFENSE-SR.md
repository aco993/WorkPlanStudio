# WorkPlan Studio — intervju odbrana

Ovaj dokument nije skripta za preuveličavanje projekta. Kandidat treba da pokaže kod i test koji podržavaju odgovor, a ograničenje da prizna prije nego što ga intervjuista pronađe.

## Predstavljanje od 30 sekundi

WorkPlan Studio je statička Blazor WebAssembly portfolio aplikacija za proizvodne radne planove: operacije, radna mjesta, vrijeme, trošak i demonstraciono konačno-kapacitivno raspoređivanje. Tehnički najzanimljiviji dio je što EF Core i SQLite rade u browseru, dok je scheduler čista, deterministička C# biblioteka. Hardening je fokusiran na ono što se može dokazati: višeslojnu validaciju, WAL-safe persistence, recovery bez tihog gubitka podataka, all-or-nothing routing, real-SQLite i Playwright regresije. Scheduler je heuristika, AI je opcioni narrator, a projekat nije predstavljen kao production-ready sistem.

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

- Kratko: ne tvrdim da je localStorage bezbjedan; ključ se ne loguje i šalje se samo validiranom endpointu.
- Detaljno: HTTPS, localhost HTTP izuzetak, bez user-info/query/fragment, 15 s timeout i production proxy roadmap.
- Follow-up: Da li XSS može pročitati ključ? Da; zato localStorage nije secret vault i produkcija mora koristiti backend.

### 22. Zašto AI nije scheduler?

- Kratko: scheduling odluke moraju biti deterministične i testabilne.
- Detaljno: AI samo preformuliše structured facts; rule-based explanation je uvijek dostupna i provider kvar ne utiče na schedule.
- Follow-up: Šta se šalje provideru? Sažetak, bottleneck, late-job facts i preporuka, ne baza.

### 23. Šta znači SQLite advisory suppression?

- Kratko: poznat high transitive rizik je dokumentovano prihvaćen, ne ignorisan.
- Detaljno: audit ostaje aktivan za sve drugo; fixed EF queries smanjuju reachable surface, ali rizik nije nula. Ne forsira se nepodržan major override.
- Follow-up: Kada ukloniti suppression? Čim EF podrži testiran patched dependency chain.

### 24. Zašto nema ProductionOrder?

- Kratko: bolje je pošteno ograničiti scope nego napraviti pola enterprise featurea.
- Detaljno: UI više ne izlaže Explicit due date; ADR definiše quantity/release/due/priority/revision/status koje buduća cjelina mora imati.
- Follow-up: Kako bi migrirao scheduler? Mapper bi koristio orders i routing snapshot, ne master `WorkPlan` direktno.

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
- Detaljno: 250 jobs/2.000 ops/5.000 local steps je bilo ~395 ms i ~243 MB allocations na jednom desktopu; determinism je potvrđen.
- Follow-up: Zašto allocations tako rastu? Svaki neighbor klonira order i ponovo materializuje schedule/evaluation; pooling/incremental evaluation je optimization roadmap.

### 28. Koji refactoring nisi uradio?

- Kratko: nisam dodao repository/MediatR/CQRS niti fragmentirao Razor bez mjerljive koristi.
- Detaljno: ostao je direktan EF service sloj i jedan vrijedan interface za bUnit seam.
- Follow-up: Kada bi repository imao smisla? Više storage implementacija ili domain persistence port koji EF detalji stvarno krše.

### 29. Da li je aplikacija production-ready?

- Kratko: ne.
- Detaljno: nema backend auth, multi-user concurrency, server backup, prave migracije, secret vault, calendar model ni operational telemetry/SLA.
- Follow-up: Zašto onda “production-grade hardening”? Primijenjeni su production principi na failure handling i evidence, ali hosting scope ostaje portfolio demo.

### 30. Kako CI štiti kvalitet?

- Kratko: odvojeni engine, web/WASM i Playwright workflowi, warnings-as-errors i NuGet audit.
- Detaljno: deploy je test-gated, permissions su minimalne po workflowu, Dependabot prati NuGet/actions.
- Follow-up: Slabost? Deploy trenutno gateuje engine, dok PR workflowi nose web/E2E; stroži release environment bi zahtijevao sve required checks.

### 31. Zašto je `Schedule.razor` još velik?

- Kratko: veličina je poznata, ali ekstrakcija nije urađena bez jasnog state/test dobitka.
- Detaljno: page drži povezane parametre, rezultat, assistant i modal state. Sljedeći opravdan rez je parameter form ili result presentation ako dobiju zaseban behavior/reuse.
- Follow-up: Šta ne bi radio? Deset trivijalnih child komponenti koje samo prosljeđuju parametre.

### 32. Šta bi sljedeće uradio?

- Kratko: ProductionOrder ili server persistence, zavisno od cilja proizvoda.
- Detaljno: za portfolio prvo CI required-check potvrda i advisory upgrade praćenje; za proizvod backend auth/storage/migrations, pa order/calendar model i solver izbor na osnovu podataka.
- Follow-up: Koji je najrizičniji roadmap? ProductionOrder jer mijenja semantiku, storage schema, UI i scheduler input zajedno.

## Slabosti koje kandidat treba sam da prizna

- Browser storage nije migracija, backup ni multi-device sync.
- Scheduler radi na UI threadu i heuristički je; nema optimality gap.
- WorkPlan trenutno glumi demonstration job; nije customer production order.
- AI ključ u localStorageu je kompromis samo za BYOK demo.
- SQLite advisory je prihvaćen i praćen, ne riješen.
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
