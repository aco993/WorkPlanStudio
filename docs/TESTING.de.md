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
    E2E["🌐 <b>E2E</b> — Playwright · 11 Tests<br/>Chromium, Reload/Reset, Tastatur, Mobile und Sprache"]
    WEB["🧩 <b>Daten + Grenze + Komponenten</b> — xUnit/bUnit · 67 Tests<br/>echtes SQLite, Validierung, Mapping, UI &amp; Assistent"]
    UNIT["⚙️ <b>Unit + Property + Architektur</b> — xUnit/CsCheck · 135 Tests<br/>Engine, Limits, Invarianten &amp; Designregeln"]

    E2E --> WEB --> UNIT

    classDef fast fill:#dcfce7,stroke:#16a34a,color:#14532d;
    classDef slow fill:#fef3c7,stroke:#b45309,color:#7c2d12;
    class UNIT fast;
    class WEB,E2E slow;
```

## Die Schichten

| Schicht | Projekt | Tests | Sichert | WASM nötig? | Laufzeit |
| --- | --- | --: | --- | :---: | --- |
| Engine + Property + Architektur | `tests/WorkPlanStudio.Scheduling.Tests` | 135 | Determinismus, Zulässigkeit, Regeln, Limits, Overflow, Cancellation, Erklärungen und die reine Architekturgrenze | nein | ~9 s |
| Daten + Mapping + Komponenten + Assistent | `tests/WorkPlanStudio.Web.Tests` | 67 | echtes SQLite, CRUD/Constraints/Recovery, vollständiges Routing-Mapping, UI-Zustände, Accessibility und gestubbter KI-Transport | ja¹ | ~14 s |
| End-to-End | `tests/WorkPlanStudio.E2E` | 11 | Chromium: Planung, Determinismus, Sprache/`html lang`, ungültige Eingaben, Save→Reload, Reset, Modal-Tastatur/Fokus und Mobile Drawer | Browser² | ~1,5 min |

¹ Diese referenzieren das Blazor-App-Assembly, daher kompiliert ihr Build die App (also `wasm-tools`). Die Tests selbst laufen auf einem normalen Host.
² Braucht einen Chromium-Download (`playwright install`) und die laufende App; kein `wasm-tools`, wenn ein vorab veröffentlichter Build ausgeliefert wird.

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

# E2E — App starten, Browser einmalig installieren, dann ausführen:
dotnet run --project src/WorkPlanStudio/WorkPlanStudio.csproj &           # liefert http://localhost:5235
pwsh tests/WorkPlanStudio.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test tests/WorkPlanStudio.E2E/WorkPlanStudio.E2E.csproj
```

Nützliche Umgebungsvariablen für E2E: `E2E_BASE_URL` (Standard `http://localhost:5235`),
`HEADED=1` um den Browser zu sehen, `E2E_ARTIFACTS=<Verzeichnis>` um Screenshots zu sammeln.

## Abdeckung

Der Engine-Job misst die Code-Abdeckung mit dem Collector der Microsoft Testing Platform; die Planungsbibliothek liegt bei etwa **98 % Zeilen / 92 % Zweige**. Lokal reproduzierbar mit:

```bash
dotnet test tests/WorkPlanStudio.Scheduling.Tests/WorkPlanStudio.Scheduling.Tests.csproj \
  --coverage --coverage-output-format cobertura
```

## In der CI

| Workflow | Läuft | Wann |
| --- | --- | --- |
| [`ci.yml`](../.github/workflows/ci.yml) | Engine-Tests (ohne WASM) + Mapper-/Komponententests (mit WASM) als zwei Jobs | bei jedem Pull Request |
| [`e2e.yml`](../.github/workflows/e2e.yml) | baut, liefert die App aus, installiert Chromium, führt Playwright aus, lädt Screenshots hoch | bei jedem Pull Request |
| [`deploy.yml`](../.github/workflows/deploy.yml) | Engine-Tests sichern das GitHub-Pages-Deploy ab | Push auf `main` |
