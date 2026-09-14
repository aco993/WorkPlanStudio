# Mitwirken

[English](CONTRIBUTING.md) · **Deutsch**

Dies ist ein Portfolio-Projekt, aber es ist wie ein echtes gebaut — Beiträge (und
neugierige Leser) sind also sehr willkommen. Diese Anleitung ist die Kurzfassung;
die Einzelheiten stehen im [README](README.de.md), in
[`docs/ARCHITECTURE.de.md`](docs/ARCHITECTURE.de.md),
[`docs/SCHEDULING.de.md`](docs/SCHEDULING.de.md) und
[`docs/TESTING.de.md`](docs/TESTING.de.md).

## Bauen und testen

```bash
# Voraussetzungen: das .NET-10-SDK und — nur zum Bauen oder Ausführen der Browser-App — die WASM-Workload:
dotnet workload install wasm-tools

# Die App starten
dotnet run --project src/WorkPlanStudio/WorkPlanStudio.csproj

# Die schnellen Tests (kein Browser, kein WASM)
dotnet test tests/WorkPlanStudio.Scheduling.Tests/WorkPlanStudio.Scheduling.Tests.csproj
dotnet test tests/WorkPlanStudio.WorkingTime.Tests/WorkPlanStudio.WorkingTime.Tests.csproj
dotnet test tests/WorkPlanStudio.Export.Tests/WorkPlanStudio.Export.Tests.csproj
dotnet test tests/WorkPlanStudio.Api.Tests/WorkPlanStudio.Api.Tests.csproj

# Die Web-Suite referenziert die Blazor-App, ihr Build übersetzt die App also mit
dotnet test tests/WorkPlanStudio.Web.Tests/WorkPlanStudio.Web.Tests.csproj
```

Die Browser-Suite (Playwright-Abläufe, axe-Prüfungen der Barrierefreiheit,
Bild-Baselines) ist in [`docs/TESTING.de.md`](docs/TESTING.de.md) beschrieben.

## Vor dem Push — ausführen, was die CI ausführt

Jeder dieser Punkte ist ein Gate im Pull Request, und alle vier laufen lokal:

```bash
dotnet format WorkPlanStudio.slnx --severity warn --verify-no-changes   # 25 durchgesetzte Stilregeln
python3 -m unittest discover -s .github/scripts -p 'test_*.py'          # die Tests der Prüfskripte selbst
python3 .github/scripts/check_doc_links.py                              # jeder relative Link *und* Anker
python3 .github/scripts/check_resources.py                              # beide .resx parsen und stimmen Schlüssel für Schlüssel überein
```

`dotnet format WorkPlanStudio.slnx --severity warn` (ohne `--verify-no-changes`)
behebt das meiste, was der erste Punkt meldet.

## Wie der Code gegliedert ist

- `src/WorkPlanStudio.Scheduling` — die **reine** Planungsengine, einschließlich des
  exakten Branch-and-Bound in `Exact/`. Sie darf Blazor, EF Core, JS-Interop und
  WebAssembly nicht referenzieren; ein Architekturtest setzt das durch. Neuer
  Algorithmuscode gehört hierher und wird direkt getestet.
- `src/WorkPlanStudio.WorkingTime` — die **reine** Arbeitszeit-Bibliothek:
  Schichtmodelle, die ArbZG-Vorschriften als Parameter, Feiertage, die Zeitleiste,
  aus der ein Maschinenkalender wird, und die Compliance-Auswertung. Dieselbe
  Grenzregel wie bei der Engine.
- `src/WorkPlanStudio.Export` — die **reinen** CSV-, xlsx- und PDF-Writer. Sie haben
  überhaupt keine Paketreferenz, und das ist der Sinn; eine hinzuzufügen braucht eine
  ADR.
- `src/WorkPlanStudio` — die Blazor-App. Der `ScheduleMapper` ist die Grenze, die aus
  EF-Entitäten Eingaben der Engine macht (und die eine Stelle, an der `decimal` zu
  ganzen Sekunden wird); `ShopCalendar` macht aus den Betriebseinstellungen Kalender.
- `src/WorkPlanStudio.Contracts` / `src/WorkPlanStudio.Api` — das optionale Backend
  und die DTOs, die es mit dem Client teilt. **Die API kompiliert `Models/**`,
  `Validation/**` und vier `Services/*.cs`-Dateien als verlinkten Quelltext aus der
  Browser-App**; gibt man also etwas in diesen Ordnern eine Abhängigkeit von Blazor,
  EF Core oder JS-Interop, bricht ein Projekt, das man gar nicht angefasst hat.
- `tests/` — Engine, Arbeitszeit, Export, Web, Backend, die Browser-Suite, der
  gemeinsame Problemgenerator und die Benchmarks.

## Konventionen

- **Der Stil** steht in [`.editorconfig`](.editorconfig) und ist ein echtes Gate:
  25 Regeln auf Warnstufe, durchgesetzt von `dotnet format --severity warn` in der
  CI. Private `const`- und `static readonly`-Felder sind PascalCase, jedes andere
  private Feld `_camelCase`, und `readonly` ist Pflicht bei einem Feld, das nie neu
  zugewiesen wird.
- **Der Build behandelt Warnungen als Fehler** — halten Sie ihn sauber.
- **Paketversionen** werden zentral in
  [`Directory.Packages.props`](Directory.Packages.props) verwaltet; die Version
  gehört dorthin, die `.csproj` referenziert das Paket ohne Version.
- **Tests ergänzen oder anpassen** bei jeder Verhaltensänderung, und zwar bevorzugt
  einen Test, der ohne die Änderung scheitert. Ein Test, der nicht scheitern kann,
  ist schlechter als kein Test, weil er wie Abdeckung aussieht.
- **Wesentliche Entscheidungen festhalten**, als neue ADR in
  [`docs/adr/`](docs/adr/README.de.md), mit einer Zeile im englischen *und* im
  deutschen Verzeichnis.
- **Oberflächentexte sind lokalisiert** — jeder Schlüssel gehört an dieselbe Stelle
  in `SharedResource.resx` *und* `SharedResource.de.resx`. `check_resources.py` lässt
  den Build scheitern, wenn die beiden Schlüsselmengen auseinanderlaufen.
- **Nie eine gemessene Zahl nennen, die man nicht gemessen hat.** Jede Zahl in der
  Dokumentation nennt das Artefakt, das sie erzeugt — eine Messung, einen Test, einen
  Werkzeugaufruf. Wer sie nicht erzeugen kann, beschreibt stattdessen das Verhalten.

## Wenn Sie die Browser-Suite anfassen

- Eine neue E2E-Klasse nimmt `IClassFixture<PlaywrightFixture>` und **kein**
  `[Collection]`-Attribut; die Klassen laufen parallel, jede mit eigenem Browser.
- Eine neue Route in `AccessibilityE2ETests.Routes` muss in Englisch hell, Englisch
  dunkel **und** Deutsch axe-sauber sein — das sind drei weitere Tests, und die
  Best-Practice-Regeln zählen mit.
- Ein neuer Bildschirm in `VisualRegressionTests.Matrix` braucht eine eingecheckte
  **Linux**-Baseline. Aus der CI heraus lässt sie sich nicht erzeugen, und eine
  fehlende Baseline lässt den Build absichtlich scheitern. Baselines werden bewusst
  mit `UPDATE_VISUAL_BASELINES=1` unter Linux erneuert; unter Windows und macOS
  werden die Bildtests übersprungen (siehe
  [ADR 0021](docs/adr/0021-visual-baselines-linux-only.md)).
- Jeder bUnit-Komponententest leitet von `AppBunitContext` ab. Niemals ein Element
  suchen und in einer zweiten Anweisung ein Ereignis darauf auslösen — beides gehört
  in ein `cut.InvokeAsync(...)`, sonst kehrt ein Wettrennen um veraltete Handler
  zurück.

## Pull Requests

[`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md) listet jede
Prüfung und woran sie scheitert. Eine Änderung mit sichtbarer Wirkung sollte sagen,
wie sie ausgeübt wurde — in einem Browser, mit einem Test, mit einem Befehl — statt
dass sie „eigentlich funktionieren müsste“.
