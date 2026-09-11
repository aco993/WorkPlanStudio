# Leistung

[English](PERFORMANCE.md) · **Deutsch**

Drei Arten von Messung, jede mit ihrem eigenen Werkzeug und ihrem eigenen Vorbehalt.
Keine davon ist eine Kapazitätszusage für den Produktivbetrieb; alle sind mit einem
Befehl reproduzierbar.

| Was | Werkzeug | Wo | Blockiert? |
| --- | --- | --- | --- |
| Durchsatz von Engine, Kandidatenbewertung und Arbeitszeit | BenchmarkDotNet (`tests/WorkPlanStudio.Benchmarks`) | [`performance.yml`](../.github/workflows/performance.yml), wöchentlich und auf `main` | berichtet |
| Stolperdrähte gegen Regressionen | xUnit-Budgets (`PerformanceBudgetTests` in beiden Bibliotheks-Testprojekten) | `performance.yml` auf `main`, wöchentlich, auf Anforderung | **lässt den Build scheitern** |
| Seitenaufbau der veröffentlichten Seite | Lighthouse CI gegen das veröffentlichte `wwwroot`, ausgeliefert wie von GitHub Pages | `performance.yml` | Barrierefreiheit, Best Practices, SEO und Layoutverschiebung **blockieren**; der Leistungswert ist eine Warnung |

## Die Maschine, von der diese Zahlen stammen

Jede Zahl unten wurde am **11.09.2026** auf dem Stand gemessen, mit dem dieses
Dokument ausgeliefert wird: Intel Core i7-14700K (28 logische, 20 physische Kerne),
Windows 11 26200, .NET SDK 10.0.301 / Laufzeit 10.0.9, BenchmarkDotNet 0.15.8,
`--job short` (3 Aufwärm- und 3 gemessene Durchläufe). Sie sind Anhaltswerte und nur
gegen sich selbst auf derselben Maschine vergleichbar — ein Browser auf einem Telefon
ist eine andere Welt, und dafür spricht der Abschnitt zum Seitenaufbau.

```bash
dotnet run -c Release --project tests/WorkPlanStudio.Benchmarks -- --filter '*' --job short
```

## Ganze Läufe

| Messung | Mittelwert | Belegter Speicher |
| --- | ---: | ---: |
| klein: 25 Aufträge, 4 Neustarts, 500 Suchschritte | 1,33 ms | 19,1 KB |
| mittel: 100 Aufträge, nur Regel (eine Einplanung von 600 Arbeitsgängen) | 31,2 µs | 84,9 KB |
| mittel: 100 Aufträge, 8 Neustarts, 2 000 Suchschritte | 91,1 ms | 91,7 KB |
| mittel + Schichtkalender, Pausen, Sperrzeiten | 239,9 ms | 91,8 KB |
| groß: 250 Aufträge, 16 Neustarts, 5 000 Suchschritte | 1,59 s | 289,3 KB |

**Die Speicherspalte ist die interessante, und sie ist flach.** Der mittlere Lauf
bewertet 16 008 Kandidatenreihenfolgen und belegt insgesamt 92 KB — nicht je
Kandidat, insgesamt. Früher war es etwa ein Gigabyte, weil jeder Kandidat ein
vollständiges `Schedule`-Objekt baute und darauf jede Kennzahl berechnete. Die Suche
bewertet Kandidaten jetzt in einen Arbeitsbereich, den sie für den ganzen Lauf
wiederverwendet, und baut genau einen `Schedule`: den, den sie behält.

## Je Kandidat

Die Einheit, aus der die gesamte Suche besteht — direkt gemessen statt aus einem
ganzen Lauf herausgerechnet, denn genau so kam die Zahl zustande, die dieses Dokument
früher veröffentlichte und die um den Faktor 7,6 falsch war:

| Aufträge | einen Kandidaten in den Arbeitsbereich bewerten | einplanen und einen `Schedule` erzeugen | bewerten und jede Kennzahl aufsummieren |
| ---: | --- | --- | --- |
| 25 | 1,53 µs · **0 B** | 2,89 µs · 17 128 B | 3,78 µs · 18 824 B |
| 100 | 6,10 µs · **0 B** | 13,34 µs · 66 624 B | 13,35 µs · 68 320 B |
| 250 | 15,79 µs · **0 B** | 27,30 µs · 165 624 B | 33,06 µs · 167 400 B |

Die erste Spalte ist das, was die lokale Suche aufruft, zehntausendfach je Lauf. Die
dritte ist das, was sie *früher* aufrief. `AllocationBudgetTests` fixiert die Null,
sodass sie eine Eigenschaft ist und nicht eine Beobachtung von einem Nachmittag.

Zum Rest der Tabelle:

- **Eine Einplanung ist billig.** 31,2 µs setzen 600 Arbeitsgänge, bauen den Plan und
  berechnen jede Kennzahl. Alles darüber ist der Optimierer.
- **Kalender kosten den Faktor 2,6** beim mittleren Problem — 91,1 ms ohne sie,
  239,9 ms mit Schichten, Pausen und Sperrzeiten —, weil jede Belegung die
  Zeitfenster durchläuft, die sie überstreicht, Pausen überbrückt und Sperrzeiten
  überspringt. Zusätzlichen Speicher belegt sie nicht. Weiterhin interaktiv, und es
  ist der ehrliche Preis der Funktion, die den Plan überhaupt aussagekräftig macht.
- **Auslastung, mittlere Durchlaufzeit, Termintreue und Maximalverspätung werden
  einmal berechnet**, für den Plan, der behalten wird, nicht für jeden Kandidaten.
  Sie skalierten mit dem Planungshorizont statt mit der Instanz, was 29 % der Kosten
  je Kandidat ausmachte — für Angaben, die die Suche nie liest.

## Die Arbeitszeit-Bibliothek

| Messung | Mittelwert | Belegter Speicher |
| --- | ---: | ---: |
| Feiertage: alle 16 Länder, ein Jahr *(gepuffert — siehe unten)* | 81,2 ns | 88 B |
| Zeitleiste: Dreischichtmodell, 400 Tage → Maschinenkalender | 14,2 µs | 89,5 KB |
| Zeitleiste: eine Schicht mit einer Abwesenheit, 400 Tage | 6,0 µs | 39,3 KB |

**Die Feiertagszeile misst den Zwischenspeicher, nicht die Berechnung**, und das zu
sagen ist wichtig, weil die Zahl sonst nichts bedeutet: `GermanHolidays` merkt sich
Ergebnisse je `(Jahr, Land, Teilfeiertage)`, und die Messung fragt in jedem Durchlauf
2026 ab — nach dem ersten ist es ein Wörterbuchzugriff. Die Berechnung ist stattdessen
durch einen Stolperdraht begrenzt:
`Holidays_for_every_state_and_a_decade_compute_within_their_budget` berechnet zehn
ungepufferte Jahre × 16 Länder in unter **50 ms**. Eine Zahl je Aufruf wird hier nicht
veröffentlicht, weil die eingecheckte Messung sie nicht isoliert.

Ansonsten ist Arbeitszeit in dieser Größenordnung umsonst: Die App baut vor jedem Lauf
für jeden Arbeitsplatz einen 400-Tage-Kalender neu, statt irgendetwas zu puffern.

## Stolperdrähte gegen Regressionen

`PerformanceBudgetTests` sind keine Benchmarks. Ihre Schranken liegen rund eine
Größenordnung über den Messungen oben — das mittlere Problem unter 3 s, mit Kalendern
unter 4 s, eine große Einplanung nach reiner Regel unter 0,5 s, eine 400-Tage-Zeitleiste
unter 50 ms, `Segments` über den ganzen Fünfjahresbereich unter 150 ms, die
Compliance-Auswertung über 400 Tage unter 100 ms — jeweils nach einem Aufwärmlauf
gemessen. Ein Bestehen sagt nichts Genaues; ein Scheitern heißt, dass etwas quadratisch
geworden ist, und das ist die Regression, die sich zu fangen lohnt, ohne das Rauschen
eines Benchmarks auf einem geteilten Runner.

Sie laufen in [`performance.yml`](../.github/workflows/performance.yml) auf `main`,
wöchentlich und auf Anforderung und sind **aus dem Pull-Request-Gate ausgenommen**: Ein
Zeitstolperdraht, der einmal im Monat einen Pull Request rot färbt, bringt dem Autor
bei, neu zu starten statt hinzusehen, und dann ist er Rauschen statt Signal.

## Seitenaufbau

Die App liefert .NET-Laufzeit, EF Core und SQLite als WebAssembly aus. Veröffentlicht
sind das **103 Dateien und 19,8 MB unkomprimiert**, davon **5,8 MB** als vorkomprimierte
Brotli-Geschwister — der Lighthouse-Leistungswert ist also der einer
WebAssembly-Laufzeit, und er wird berichtet statt blockiert.

Gemessen am 11.09.2026 mit Lighthouse 13.4.1, Desktop-Voreinstellung, gegen das
veröffentlichte `wwwroot`, mit Brotli ausgeliefert wie in `performance.yml`:

| Seite | Leistung | Barrierefreiheit | Best Practices | SEO | FCP | LCP | TBT | CLS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `/` | 36 | 100 | 100 | 100 | 0,3 s | 7,7 s | 2 134 ms | 0,000 |
| `/schedule` | 36 | 100 | 100 | 100 | 0,3 s | 7,7 s | 2 210 ms | 0,001 |
| `/working-time` | 36 | 100 | 100 | 100 | 0,3 s | 7,8 s | 2 179 ms | 0,000 |

So lässt sich das nachstellen:

```bash
dotnet publish src/WorkPlanStudio/WorkPlanStudio.csproj -c Release -o release
npx http-server release/wwwroot -p 8080 -P "http://localhost:8080?" --gzip --brotli -s &
npx lighthouse http://localhost:8080/ --preset=desktop
```

**Sechsunddreißig ist die Zahl, und sie gehört der Laufzeit, nicht der Anwendung.** Der
Startschirm zeichnet nach 0,3 s; der größte Inhaltsaufbau ist die Übersicht, nachdem
die Laufzeit geladen und gestartet ist, SQLite geöffnet und der Demo-Betrieb angelegt
wurde. Zwei Sekunden Blockierzeit sind das Übersetzen des WebAssembly-Moduls. Die
Layoutverschiebung ist praktisch null, Barrierefreiheit und Best Practices sind
vollständig — und diese drei **werden** blockierend geprüft. Der Leistungswert ist der
eine, der veröffentlicht wird, ohne verteidigt zu werden.

Die Hebel, nach Wirkung geordnet, falls sich das je bewegen muss: die Assemblies
nachladen, die der erste Bildschirm nicht braucht, eine kleinere Laufzeit über
Trimming-Einstellungen, die das Relinken von nativem SQLite derzeit einschränkt — und
nicht mehr auf der Liste: ein Web Worker für die Planung, denn
[ADR 0019](adr/0019-off-thread-scheduling.md) hält fest, dass sich diese App mit
Threads überhaupt nicht bauen lässt, solange SQLite in das Modul gelinkt ist.

## Komplexität und Grenzen

Eine Vorwärtseinplanung ist ungefähr `O(Arbeitsgänge × Kapazität)`, weil jeder Schritt
die Plätze des Arbeitsplatzes durchsieht; mit Kalendern durchläuft jede Belegung
zusätzlich die Zeitfenster, die sie überstreicht. Der Multi-Start vervielfacht die
Einplanungskosten mit seiner Anzahl. Die lokale Suche bewertet bis zum eingestellten
Nachbarschaftsbudget und bewertet jeden Kandidaten neu, ihre praktische obere Schranke
ist also etwa `O(Suchschritte × Arbeitsgänge × Kapazität)`. Bewertung und
Terminvergabe sind linear in Aufträgen und Arbeitsgängen.

`SchedulingParameterLimits` begrenzt 64 Multi-Start-Läufe, 20 000 Schritte der lokalen
Suche **und ihr Produkt auf 200 000 Kandidatenpläne**. Die Produktgrenze ist die
wichtige: Die beiden Faktoren waren unabhängig voneinander begrenzt, das Formular
konnte also 1 280 000 Einplanungen verlangen, ohne dass ein einzelner Wert
unvernünftig aussah. Die Seite prüft das Produkt, bevor sie den Dienst ruft, und nennt
die Zahl, die entstanden wäre. 200 000 Kandidaten des 100-Auftrags-Problems kosten auf
dieser Maschine etwa eine Sekunde — das ist als Obergrenze zu lesen, nicht als
Empfehlung: Ein Browser ist langsamer als diese Maschine.

Einen Plan als optimal zu beweisen ist ein anderes Budget und ein anderes Versprechen.
Das Branch-and-Bound in `WorkPlanStudio.Scheduling.Exact` ist eine enge Schleife ohne
Rückgabe an den Browser, friert den Tab also genau so lange ein, wie es darf; die Seite
gibt ihm **zwei Sekunden**, live auf dem Demo-Betrieb gemessen, und berichtet die
bewiesene untere Schranke, wenn das nicht reicht. Zehn Sekunden wurden ausprobiert und
sind zu lang, um eine Seite einzufrieren. Siehe [ADR 0015](adr/0015-exact-solver.md).

Eine serverseitige Planung lohnt sich, sobald Pläne geteilt, dauerhaft, nachvollziehbar
oder langlaufend sein sollen — und das optionale Backend in `src/WorkPlanStudio.Api` ist
der Ort dafür. OR-Tools CP-SAT oder ein MILP lohnen sich, sobald globale Schranken,
alternative Maschinen oder harte Lieferrestriktionen wichtiger sind als die Einfachheit
der Heuristik; der exakte Löser schreibt die LP-Datei bereits, der erste Schritt auf
diesem Weg ist also ein Befehl und kein Umbau.
