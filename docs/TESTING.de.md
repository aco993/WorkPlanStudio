# Teststrategie

[English](TESTING.md) · **Deutsch**

Die anspruchsvolle Logik dieses Projekts ist die Planungs-Engine — dort liegt
daher der Schwerpunkt der Tests. Leitidee ist eine **Testpyramide**: viele
schnelle, deterministische Tests unten gegen reinen Code und wenige langsame Tests
mit hoher Aussagekraft oben gegen die echte App im echten Browser.

Dass die Engine eine reine Bibliothek ist (kein Blazor, kein EF, kein WebAssembly),
macht das erst möglich — der Großteil der Suite läuft in **wenigen Sekunden**, ohne
Browser und ohne die `wasm-tools`-Workload.

```mermaid
graph TD
    PROD["🚢 <b>Production E2E</b> — Playwright + Docker · 3 Tests<br/>Login, Auth, Sprache und Mobile"]
    E2E["🌐 <b>Offline E2E</b> — Playwright · 10 Tests<br/>Chromium, Reload/Reset, Tastatur, Mobile und Sprache"]
    PG["🐘 <b>PostgreSQL</b> · 2 Tests<br/>Migrationen, Claims und Lease-Fencing"]
    API["🔐 <b>API-Integration</b> · 12 Tests<br/>Identity, CSRF und Owner-Isolation"]
    WEB["🧩 <b>Daten + Grenze + Komponenten</b> — xUnit/bUnit · 55 Tests<br/>echtes SQLite, Validierung, Mapping, UI &amp; Assistent"]
    UNIT["⚙️ <b>Unit + Property + Architektur</b> — xUnit/CsCheck · 102 Tests<br/>Engine, Kalender, Setup, Limits, Invarianten &amp; Designregeln"]

    PROD --> E2E --> PG --> API --> WEB --> UNIT

    classDef fast fill:#dcfce7,stroke:#16a34a,color:#14532d;
    classDef slow fill:#fef3c7,stroke:#b45309,color:#7c2d12;
    class UNIT fast;
    class WEB,API,PG,E2E,PROD slow;
```

## Die Schichten

| Schicht | Projekt | Tests | Sichert | WASM nötig? | Laufzeit |
| --- | --- | --: | --- | :---: | --- |
| Engine + Property + Architektur | `tests/WorkPlanStudio.Scheduling.Tests` | 102 | Determinismus, Zulässigkeit, Regeln, Kalender/Setup, bounded Exact Search, Limits, Overflow, Cancellation und Architekturgrenze | nein | ~8 s |
| Daten + Mapping + Komponenten + Assistent | `tests/WorkPlanStudio.Web.Tests` | 55 | echtes SQLite, CRUD/Constraints/Recovery, vollständiges Routing-Mapping, UI-Zustände, Accessibility und gestubbter KI-Transport | ja¹ | ~12 s |
| Production API | `tests/WorkPlanStudio.Api.Tests` | 12 | migriertes SQLite, Identity/MFA/Reset, CSRF, Owner-Isolation, Worker und Health | ja¹ | ~35 s |
| PostgreSQL-Integration | `tests/WorkPlanStudio.Postgres.Tests` | 2 | echte Migrationen, konkurrierender Claim, Lease-Übernahme und stale-owner Fencing | nein³ | ~10 s |
| Offline-End-to-End | `tests/WorkPlanStudio.E2E` | 10 | Chromium: Planung, Sprache, ungültige Eingaben, Save→Reload, Reset, Tastatur und Mobile | Browser² | ~3 min |
| Production-End-to-End | `tests/WorkPlanStudio.ProductionE2E` | 3 | Container-Login, authentifizierte Navigation, Accessibility-Semantik, Deutsch und Mobile Drawer | Browser² | ~20 s |

¹ Diese referenzieren das Blazor-App-Assembly, daher kompiliert ihr Build die App (also `wasm-tools`). Die Tests selbst laufen auf einem normalen Host.
² Braucht einen Chromium-Download (`playwright install`) und die laufende App; kein `wasm-tools`, wenn ein vorab veröffentlichter Build ausgeliefert wird.
³ Benötigt explizit `WPS_POSTGRES_CONNECTION`; die CI stellt PostgreSQL 18 bereit. Ohne Variable wird bewusst übersprungen, niemals still durch SQLite ersetzt.

## Was jede Schicht tut

### ⚙️ Unit + Architektur — die Engine

Der Kern: Zulässigkeit (Reihenfolge, Kapazität, Freigabezeiten), je ein fokussierter
Test pro **Prioritätsregel** und pro **Zieltermin-Regel**, die KPIs des Evaluators
und die Such-Garantien („nie schlechter als die Regel", „mehr Starts schaden nie",
„Lokalsuche verschlechtert nie"). Determinismus wird dreifach festgenagelt:

- ein **Golden-Value**-Test des PRNG (`DeterministicRandom`),
- *gleicher Seed → identischer Plan*,
- *identischer Plan unabhängig von der Eingabe-Reihenfolge* (schützt gegen
  versehentliches Verlassen auf Dictionary-/HashSet-Reihenfolge — eine echte
  Desktop-vs-WASM-Falle).

`ArchitectureTests` reflektieren über das Engine-Assembly und **lassen den Build
fehlschlagen**, falls jemand Blazor, EF Core, JS-Interop oder SQLite daraus
referenziert. Die Pure-Library-Grenze ist die Designentscheidung, auf der die ganze
Pyramide ruht — also wird sie per Test erzwungen, nicht der Disziplin überlassen.

### 🎲 Eigenschaftsbasiert — Invarianten

Beispieltests prüfen die Fälle, an die man gedacht hat; **Property-Tests prüfen die
anderen.** Mit [CsCheck](https://github.com/AnthonyLloyd/CsCheck) erzeugt jeder Test
hunderte zufällige, aber gültige Planungsprobleme und prüft eine *Invariante*, die
für jeden erzeugbaren Plan gelten muss: Reihenfolge (kein Schritt startet vor Ende
des vorigen), Kapazität (kein Arbeitsplatz überschreitet seine Slots), Determinismus,
eine Makespan-Untergrenze und „nie schlechter als die reine Regel". Bei einem Fehler
*schrumpft* CsCheck auf ein minimales Gegenbeispiel und nennt einen Seed zum
Reproduzieren.

### 🔌 Grenze — das Mapping

`ScheduleMapper` ist die einzige Stelle, an der `decimal`-Minuten zu ganzzahligen
Sekunden werden. Diese Tests nutzen handgebaute `WorkPlan`-/`Operation`-/`WorkCenter`-Entitäten
(keine Datenbank), um Rundung, Overflow, Kapazität und das All-or-nothing-Prinzip
zu prüfen: Ein inaktiver oder fehlender Arbeitsplatz lehnt den vollständigen Plan
mit einem stabilen Diagnosecode ab, statt einen Arbeitsgang still zu entfernen.

`BrowserDatabaseTests` verwenden echtes dateibasiertes SQLite für Constraints,
CRUD, Save→Reload, Schema-Mismatch, beschädigte Payloads, Export/Reset sowie
simulierte Lese-, Schreib- und Quota-Fehler.

### 🧩 Komponenten — die Seite

[bUnit](https://bunit.dev) rendert `Schedule.razor` im Speicher gegen einen
**gefälschten** `IProductionScheduleService`, also ohne Datenbank und ohne
Engine-Lauf. Es prüft, dass ein Ergebnis in die richtigen KPI-Karten, Gantt-Zeilen
und Tabellenzeilen verwandelt wird; dass der Leerzustand ohne Daten erscheint; dass
verspätete Aufträge rote Pillen und Balken erhalten; und dass ein Klick auf
**Generieren** den Service mit den im Formular gewählten Parametern aufruft. Genau
deshalb hängt die Seite an der `IProductionScheduleService`-*Schnittstelle* — damit
ein Test eine Fälschung einsetzen kann.

### 🤖 Assistent — Erzählung & Fallback

Der [Planungs-Assistent](AI-ASSISTANT.md) wird ohne Netzwerk getestet. Der
regelbasierte Erzähler wird direkt geprüft (deterministische Zeilen und Tonalität).
Der optionale KI-Erzähler läuft gegen einen **gestubbten `HttpMessageHandler`**: der
Test prüft, dass die Anfrage den Schlüssel und die berechneten Fakten trug, und
liefert eine vorgefertigte Antwort zurück — ein `500` prüft, dass eine Ausnahme
fliegt. Die `ScheduleAssistant`-Fassade wird für alle drei Pfade getestet: nicht
konfiguriert, KI gesund und KI fehlerhaft (Fallback auf den regelbasierten Text).

### 🌐 End-to-End — die echte Sache

[Playwright](https://playwright.dev/dotnet/) steuert Chromium gegen die laufende App
über ein kleines Page-Object (`SchedulePage`). Die Kernprüfung ist die vom Auftrag
geforderte: **die Zieltermine anziehen, und der Plan wird sichtbar verspätet** — rot
umrandete Balken und rote Status-Pillen (`schedule-ontime.png` → `schedule-late.png`,
vom Lauf selbst erzeugt). Außerdem wird geprüft, dass eine andere Prioritätsregel
einen zulässigen Plan behält und ihn sichtbar verändert, dass derselbe Seed denselben
Makespan reproduziert und dass die Oberfläche auf Deutsch umschaltet.

## Tests ausführen

```bash
# Alles außer E2E (schnell, ohne Browser):
dotnet test tests/WorkPlanStudio.Scheduling.Tests/WorkPlanStudio.Scheduling.Tests.csproj
dotnet test tests/WorkPlanStudio.Web.Tests/WorkPlanStudio.Web.Tests.csproj
dotnet test tests/WorkPlanStudio.Api.Tests/WorkPlanStudio.Api.Tests.csproj

# E2E — App starten, Browser einmalig installieren, dann ausführen:
dotnet run --project src/WorkPlanStudio/WorkPlanStudio.csproj &           # liefert http://localhost:5235
pwsh tests/WorkPlanStudio.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test tests/WorkPlanStudio.E2E/WorkPlanStudio.E2E.csproj
```

Nützliche Umgebungsvariablen für E2E: `E2E_BASE_URL` (Standard `http://localhost:5235`),
`HEADED=1` um den Browser zu sehen, `E2E_ARTIFACTS=<Verzeichnis>` um Screenshots zu sammeln.

## Abdeckung

Der Engine-Job misst die Code-Abdeckung mit dem Collector der Microsoft Testing Platform. Die verifizierte Engine-Abdeckung beträgt **508/508 Zeilen und 245/245 Zweige (100 % / 100 %)** über 102 Tests. Lokal reproduzierbar mit:

```bash
dotnet test tests/WorkPlanStudio.Scheduling.Tests/WorkPlanStudio.Scheduling.Tests.csproj \
  --coverage --coverage-output-format cobertura
```

## In der CI

| Workflow | Läuft | Wann |
| --- | --- | --- |
| [`ci.yml`](../.github/workflows/ci.yml) | fail-closed Dependency-Audit, Format/Dokumente, Engine/Web/API/PostgreSQL, Migrationen, Performance, Container und Production-E2E | bei jedem Pull Request |
| [`e2e.yml`](../.github/workflows/e2e.yml) | baut, liefert die App aus, installiert Chromium, führt Playwright aus, lädt Screenshots hoch | bei jedem Pull Request |
| [`codeql.yml`](../.github/workflows/codeql.yml) | CodeQL C# mit `security-extended` | Pull Requests, `main`, wöchentlich |
| [`production-evidence.yml`](../.github/workflows/production-evidence.yml) | rate-kontrollierter Soak plus passiver OWASP-ZAP-Baseline | Pull Requests, wöchentlich, manuell |
| [`deploy.yml`](../.github/workflows/deploy.yml) | vollständiger Release-Gate vor dem GitHub-Pages-Deploy | Push auf `main` |
