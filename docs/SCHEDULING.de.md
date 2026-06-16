# Produktionsplanung — wie sie funktioniert

[English](SCHEDULING.md) · **Deutsch**

Dieses Dokument beschreibt die Planungs-Engine in
[`src/WorkPlanStudio.Scheduling`](../src/WorkPlanStudio.Scheduling). Es ist eine
eigenständige, abhängigkeitsfreie .NET-Bibliothek: kein Blazor, kein Entity
Framework, kein JavaScript, kein WebAssembly. Diese Isolation ist gewollt — sie
hält den Algorithmus auf einem normalen Runner unit-testbar und außerhalb der
Browser-App wiederverwendbar.

> **Kurzfassung** — Freigegebene Arbeitspläne werden zu *Aufträgen*; jeder
> Arbeitsgang ist ein *Schritt*, der in Reihenfolge auf einem *Arbeitsplatz* mit
> endlicher Kapazität laufen muss. Die Engine weist jedem Auftrag einen Zieltermin
> zu, plant die Arbeit so ein, dass diese Termine möglichst gehalten werden,
> optimiert die Reihenfolge und bewertet das Ergebnis. Bei gleichem Seed entsteht
> immer exakt derselbe Plan.

---

## 1. Das Problem

Dies ist ein kapazitätsbeschränktes **Job-Shop-/Flow-Shop-Scheduling-Problem**:

- Ein **Auftrag** (`ProductionJob`) ist eine geordnete Kette von **Schritten**.
- Ein **Schritt** (`JobStep`) ist ein Arbeitsgang: er läuft für eine feste Dauer
  auf einem einzigen **Arbeitsplatz** und darf erst starten, wenn der vorherige
  Schritt desselben Auftrags fertig ist (Arbeitsgang-Reihenfolge).
- Ein **Arbeitsplatz** (`MachineCapacity`) hat `ParallelCapacity` identische
  Slots; er kann höchstens so viele Arbeitsgänge gleichzeitig ausführen (die harte
  Kapazitätsbeschränkung).

Ziel ist zu entscheiden, *wann* und *auf welchem Slot* jeder Arbeitsgang läuft,
sodass Aufträge ihre Zieltermine halten und die Werkstatt schnell fertig wird.

## 2. Zeit ist ganzzahlige Sekunden

Jede Dauer und jeder Zeitpunkt ist eine `long`-Anzahl von **Sekunden ab dem
Planungshorizont** (Sekunde 0). In der Kernschleife gibt es keine Fließkommazahlen.

Warum: Die App läuft im Browser (WebAssembly), aber ihre Tests laufen auf dem
Desktop/in der CI. Fließkomma-Summen können sich in den letzten Bits zwischen
Laufzeiten unterscheiden; Ganzzahlen nicht. Ganzzahlige Zeit garantiert, dass der
in der CI verifizierte Plan *bit-identisch* zu dem im Browser erzeugten ist.

Die einzige Stelle, an der `decimal`-Minuten zu ganzzahligen Sekunden werden, ist
die Mapping-Schicht der App (`ScheduleMapper.ToSeconds`), mit explizitem
`MidpointRounding.ToEven`. Geld und die Kostenrollups bleiben `decimal` und
betreten den Scheduler nie — Kosten sind eine Anzeige-Projektion, kein
Planungs-Input.

## 3. Parameter

Alle Stellschrauben liegen in `SchedulingParameters` (unveränderlich):

| Parameter | Bedeutung |
| --- | --- |
| `DispatchRule` | Prioritätsregel auf einem umkämpften Arbeitsplatz (siehe §5) |
| `DueDateRule` | wie Zieltermine vergeben werden (siehe §4) |
| `TwkFlowFactor`, `NopSecondsPerOp`, `SlackSeconds`, `ConstantAllowanceSeconds` | regelspezifische Zieltermin-Faktoren |
| `MultiStartRuns` | Anzahl der Neustarts; Lauf 0 ist die reine Regel-Reihenfolge |
| `LocalSearchMaxSteps` | Budget für die Lokalsuche-Politur (0 schaltet sie ab) |
| `Seed` | Seed für den deterministischen PRNG |
| `MakespanWeight`, `TardinessWeight`, `LatePenalty` | Gewichte der Zielfunktion (siehe §7) |
| `MinutesPerWorkingDay` | **nur Anzeige** — bildet Arbeitszeit für das Gantt auf Kalendertage ab; keine Tagesgrenze in der Kapazität |

## 4. Zieltermin-Vergabe („Meta")

`DueDateAssigner` gibt jedem Auftrag vor der Planung einen Zieltermin. Dies sind
die klassischen OR-Regeln zur Termin-Vergabe (`release` = Freigabesekunde,
`P` = gesamte Bearbeitungssekunden, `n` = Anzahl Arbeitsgänge):

| Regel | Formel |
| --- | --- |
| **TWK** — Total Work Content | `Termin = release + Faktor · P` |
| **NOP** — Number of Operations | `Termin = release + SekundenProAG · n` |
| **SLK** — Equal Slack | `Termin = release + P + Puffer` |
| **CON** — Constant Allowance | `Termin = release + Zuschlag` |
| **Explizit** | der eigene Wert des Auftrags, sonst Rückfall auf CON |

Die Zieltermine treiben sowohl die termin-basierten Prioritätsregeln (EDD, Critical
Ratio) als auch alle Verspätungs-Kennzahlen.

## 5. Dispatch-Planung

`DispatchScheduler` verwandelt eine **Auftrags-Prioritätsreihenfolge** (eine
Permutation der Aufträge) mit einer einfachen List-Scheduling-Schleife in einen
konkreten Plan:

```
für jeden Arbeitsplatz: slotFreeAt[slot] = 0           // eine Uhr je Parallel-Slot
für jeden Auftrag in Prioritätsreihenfolge:
    jobReadyAt = job.ReleaseSeconds
    für jeden Schritt des Auftrags (in Reihenfolge):
        slot   = der früheste freie Slot des Arbeitsplatzes
        start  = max(jobReadyAt, slotFreeAt[slot])
        end    = start + step.DurationSeconds
        slotFreeAt[slot] = end
        jobReadyAt       = end
```

Zwei Invarianten gelten per Konstruktion und machen jede Ausgabe **zulässig**:

- **Reihenfolge** — der `start` eines Schritts ist `≥ jobReadyAt`, dem Ende des
  vorigen Schritts;
- **Kapazität** — die Uhr jedes Slots ist streng seriell, sodass ein Arbeitsplatz
  nie mehr als `ParallelCapacity` Arbeitsgänge gleichzeitig ausführt.

Der Scheduler ist eine reine Funktion von `(context, order)` — keine Zufälligkeit,
kein geteilter Zustand — und damit trivial reproduzierbar.

**Prioritätsregeln** (`PriorityOrdering`) erzeugen die *anfängliche* Reihenfolge,
indem Aufträge nach einem Schlüssel sortiert werden, mit der Auftrags-ID als
deterministischem Gleichstand-Brecher:

| Regel | Schlüssel (aufsteigend = höhere Priorität) |
| --- | --- |
| FIFO | Freigabezeit |
| SPT — kürzeste Bearbeitungszeit | `P` |
| LPT — längste Bearbeitungszeit | `−P` |
| EDD — frühester Termin | `Termin` |
| CR — kritisches Verhältnis | `Termin / P` (am Horizont ausgewertet) |
| WSPT — gewichtete kürzeste Zeit | `P / Gewicht` |

## 6. Multi-Start + Lokalsuche

Ein einzelner Greedy-Durchlauf ist selten optimal, daher umhüllt
`SchedulingEngine` den Dispatcher mit einer kleinen, transparenten Metaheuristik
(im Stil von GRASP):

1. **Lauf 0** nutzt die reine Regel-Reihenfolge, sodass das Ergebnis *nie schlechter
   ist als die Dispatch-Regel allein*.
2. **Läufe 1…N−1** mischen die Reihenfolge mit einem aus `(Seed, runIndex)`
   abgeleiteten Strom und behalten das beste Ergebnis. Mehr Starts können nur helfen.
3. Die **Lokalsuche** (`LocalSearch`) poliert dann die beste Reihenfolge mit
   First-Improvement-**Nachbartausch**, dispatcht und bewertet jeden Nachbarn neu.
   Sie übernimmt einen Nachbarn nur bei *strikter* Verbesserung und überschreibt
   den Amtsinhaber nie mit etwas Schlechterem — der finale Plan ist also garantiert
   `≤` dem besten Multi-Start-Ergebnis, das `≤` der Regel-Reihenfolge ist.

Weil die Lokalsuche die **Prioritätsreihenfolge** permutiert (nicht die platzierten
Arbeitsgänge) und den Dispatcher neu ausführt, ist jeder betrachtete Kandidat ein
gültiger Plan.

> **Warum die Beispieldaten sieben Aufträge haben.** Bei einem Zwei-Auftrags-Problem
> ist die Suche praktisch erschöpfend, sodass jede Prioritätsregel und jeder Seed
> zum selben Optimum konvergieren — eine Änderung *sieht* so aus, als bewirke sie
> nichts. Mit sieben Aufträgen, die um dieselben Maschinen konkurrieren, ist die
> Suche nicht mehr erschöpfend, sodass Regel und Seed den Plan sichtbar ändern. Um
> den *rohen* Effekt einer Regel (vor der Optimierung) zu sehen, setzen Sie
> **Multi-Start = 1** und **Lokalsuche = 0**.

## 7. Bewertung

`ScheduleEvaluator` fasst den Plan zu Kennzahlen und einem einzigen Strafwert
zusammen:

- **Makespan** — wann der letzte Arbeitsgang fertig ist.
- **Verspätung** — je Auftrag `max(0, Fertigstellung − Termin)`; als Summe und
  Maximum ausgewiesen.
- **Termintreue** — Anteil der Aufträge, die ihren Termin halten.
- **Auslastung** — beschäftigt ÷ (Kapazität × Makespan) je genutztem Arbeitsplatz,
  plus ein Durchschnitt.
- **Strafwert** (von der Suche minimiert), in Stunden berechnet, damit die Gewichte
  intuitiv sind:

  ```
  Strafwert = MakespanWeight · MakespanStunden
            + TardinessWeight · GesamtverspätungStunden
            + LatePenalty     · AnzahlVerspäteterAufträge
  ```

  Mit den Standardwerten dominiert die Anzahl verspäteter Aufträge, dann die
  Gesamtverspätung, dann der Makespan — also *zuerst die Termine halten, dann
  schnell fertig werden*.

## 8. Determinismus

- Alle Plan-Arithmetik ist ganzzahlig.
- Der Zufall ist ein PRNG mit festem Algorithmus (xorshift64\*, `DeterministicRandom`),
  nicht `System.Random` (dessen Algorithmus über .NET-Versionen nicht stabil ist).
  Jeder Neustart erhält seinen eigenen Strom aus `(Seed, runIndex)`, und die Engine
  läuft einsträngig.
- Die Prioritätsreihenfolge ist kanonisch (nach Schlüssel, dann Auftrags-ID
  sortiert), sodass der Plan sogar **unabhängig von der Reihenfolge ist, in der die
  Aufträge an die Engine übergeben werden**.

Diese Eigenschaften werden direkt von den Tests geprüft (Golden-PRNG-Werte,
identischer Plan bei wiederholtem Lauf, identischer Plan bei vertauschten Eingaben).

## 9. Wie die App sie nutzt

`ProductionScheduleService` (in der Blazor-App) ist die Grenze:

1. lädt die **freigegebenen** Arbeitspläne und aktiven Arbeitsplätze aus der
   Browser-Datenbank;
2. bildet Arbeitsgänge auf Schritte ab, wandelt `decimal`-Minuten in ganzzahlige
   Sekunden um und indiziert Schrittnummern neu, sodass fehlerhafte Daten den
   Vertrag der Engine nicht brechen können;
3. überspringt Arbeitsgänge auf inaktiven/unbekannten Arbeitsplätzen sowie Pläne
   ohne Schritte;
4. führt `SchedulingEngine` aus und projiziert das Ergebnis in die Gantt-Zeilen,
   die Auftragstabelle und die KPI-Karten.

## 10. Umfang & mögliche Erweiterungen

Bewusst außerhalb des Umfangs gelassen, um einfach und beweisbar korrekt zu bleiben:

- **Rückwärtsplanung (termin-verankert)** — unter geteilter endlicher Kapazität
  braucht dies einen zweiten Scheduler und kann unzulässige Pläne erzeugen;
  Vorwärtsplanung mit termin-basierten *Dispatch*-Regeln erfasst den meisten Nutzen.
- **Ein Arbeitstag-Kalender** — die Engine nutzt eine kontinuierliche Arbeitszeit-Achse;
  `MinutesPerWorkingDay` bündelt sie nur für das Gantt in Tage.
- **Maschinenanzahl je Arbeitsplatz** — die App bildet jeden Arbeitsplatz auf einen
  Slot ab, obwohl die Engine `ParallelCapacity > 1` bereits unterstützt (und die
  Tests es nutzen).
- **Reihenfolgeabhängige Rüstzeiten, Losteilung, Lückenfüllung** — alles natürliche
  nächste Schritte, keiner für eine klare, gut getestete Basis nötig.

---

*Siehe die Unit-Tests in
[`tests/WorkPlanStudio.Scheduling.Tests`](../tests/WorkPlanStudio.Scheduling.Tests)
für ausführbare Spezifikationen jeder hier beschriebenen Garantie.*
