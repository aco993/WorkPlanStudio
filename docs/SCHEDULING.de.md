# Produktionsfeinplanung — wie sie funktioniert

[English](SCHEDULING.md) · **Deutsch**

Dieses Dokument beschreibt die Planungsengine in
[`src/WorkPlanStudio.Scheduling`](../src/WorkPlanStudio.Scheduling). Sie ist eine
eigenständige, abhängigkeitsfreie .NET-Bibliothek: kein Blazor, kein Entity
Framework, kein JavaScript, kein WebAssembly. Diese Isolation ist gewollt — sie
hält den Algorithmus auf einem gewöhnlichen Runner testbar und außerhalb der
Browser-App wiederverwendbar.

> **Kurzfassung** — Freigegebene Fertigungsaufträge werden zu *Aufträgen*; jeder
> Arbeitsgang ist ein *Schritt*, der in Reihenfolge auf einem *Arbeitsplatz* mit
> endlicher Kapazität laufen muss. Die Engine vergibt jedem Auftrag einen
> Zieltermin, plant die Arbeit dagegen ein, optimiert die Reihenfolge und bewertet
> das Ergebnis. Bei gleichem Seed entsteht immer exakt derselbe Plan.

---

## 1. Das Problem

Dies ist ein kapazitätsbeschränktes **Job-Shop-/Flow-Shop-Planungsproblem**:

- Ein **Auftrag** (`ProductionJob`) ist eine geordnete Kette von **Schritten**.
- Ein **Schritt** (`JobStep`) ist ein Arbeitsgang: Er läuft für eine feste Dauer
  auf einem einzigen **Arbeitsplatz** und darf erst starten, wenn der vorherige
  Schritt desselben Auftrags fertig ist (Arbeitsgangreihenfolge).
- Ein **Arbeitsplatz** (`MachineCapacity`) hat `ParallelCapacity` gleichartige
  Plätze; er kann höchstens so viele Arbeitsgänge gleichzeitig ausführen (die
  harte Kapazitätsrestriktion).

Zu entscheiden ist, *wann* und *auf welchem Platz* jeder Arbeitsgang läuft, damit
die Aufträge ihre Zieltermine halten und die Fertigung schnell fertig wird.

## 2. Zeit sind ganzzahlige Sekunden

Jede Dauer und jeder Zeitpunkt ist eine `long`-Anzahl von **Sekunden ab dem
Planungshorizont** (Sekunde 0). **Die gesamte Belegungsarithmetik ist ganzzahlig**
— Start, Ende, Rüstzeit, Unterbrechungen, Fensterläufe und Sperrzeiten. Gleitkomma
kommt genau einmal vor, in der Zielfunktion: `ScheduleScore.Penalty` ist ein
`double` in Stunden, und der Abstieg vergleicht zwei davon mit einem Epsilon. Das
ist Absicht und es ist der gesamte Umfang; der *Plan* ist ganzzahlig, und genau das
macht ihn reproduzierbar.

Warum: Die App läuft im Browser (WebAssembly), ihre Tests laufen aber auf dem
Arbeitsplatzrechner und in der CI. Gleitkommasummen können sich in den letzten Bits
zwischen Laufzeiten unterscheiden, ganze Zahlen nicht. Ganzzahlige Belegung
garantiert, dass der in der CI geprüfte Plan *bit-identisch* zu dem im Browser
erzeugten ist.

Die einzige Stelle, an der Dezimalminuten zu ganzen Sekunden werden, ist die
Abbildungsschicht der App (`ScheduleMapper.ToSeconds`), mit ausdrücklichem
`MidpointRounding.ToEven`. Geld und die Kostensummen bleiben `decimal` und
betreten die Planung nie — Kosten sind eine Anzeigeprojektion, kein Planungsdatum.

## 3. Parameter

Alle Stellschrauben liegen in `SchedulingParameters` (unveränderlich):

| Parameter | Bedeutung |
| --- | --- |
| `DispatchRule` | Prioritätsregel an einem umkämpften Arbeitsplatz (siehe §5) |
| `DueDateRule` | wie Zieltermine vergeben werden (siehe §4) |
| `TwkFlowFactor`, `NopSecondsPerOp`, `SlackSeconds`, `ConstantAllowanceSeconds` | regelabhängige Terminfaktoren |
| `MultiStartRuns` | Anzahl der Neustarts; Lauf 0 ist die reine Regelreihenfolge |
| `LocalSearchMaxSteps` | Budget für die lokale Suche (0 schaltet sie ab) |
| `Seed` | Startwert des deterministischen PRNG |
| `MakespanWeight`, `TardinessWeight`, `LatePenalty` | Gewichte der Zielfunktion (siehe §7) |
| `LocalSearchAcceptance` | welchen verbessernden Nachbarn der Abstieg übernimmt (siehe §6 und [ADR 0022](adr/0022-local-search-acceptance.md)) |

`SchedulingParameterLimits` prüft sie alle und weist drei Dinge zurück, die das
Formular sonst verlangen könnte: mehr als `MaxMultiStartRuns` (64), mehr als
`MaxLocalSearchSteps` (20 000) und — das war die Lücke — ein **Produkt** über
`MaxTotalEvaluations` (200 000). 64 × 20 000 sind 1 280 000 Kandidatenpläne, die
kein Browser versehentlich verlangt bekommen sollte. Ebenfalls begrenzt sind die
Dauer eines einzelnen Schritts und der Planungshorizont, sodass eine falsch
abgebildete Menge zu einer Ablehnung führt statt zu einem negativen Strafwert.

Ein `MinutesPerWorkingDay` gibt es hier nicht mehr. Es war eine Konstante für die
Darstellung des Gantt-Diagramms in einer Bibliothek, deren Kernaussage lautet, dass
sie keine Oberflächenbelange kennt; es ist jetzt ein Feld der Planungsseite und
reist neben den Parametern statt in ihnen.

## 4. Zieltermine („Meta“)

`DueDateAssigner` gibt jedem Auftrag vor der Planung einen Zieltermin. Das sind die
klassischen Terminvergaberegeln des Operations Research (`release` =
Freigabesekunde, `P` = gesamte Bearbeitungssekunden, `n` = Anzahl Arbeitsgänge):

| Regel | Formel |
| --- | --- |
| **TWK** — Total Work Content | `Termin = release + Faktor · P` |
| **NOP** — Number of Operations | `Termin = release + SekundenJeArbeitsgang · n` |
| **SLK** — Equal Slack | `Termin = release + P + Puffer` |
| **CON** — Constant Allowance | `Termin = release + Zuschlag` |
| **Explizit** | der eigene Termin des Auftrags, sonst Rückfall auf CON |

`Explicit` ist die Vorbelegung, seit ein freigegebener Fertigungsauftrag einen
echten Kundentermin mitbringt (siehe
[ADR 0011](adr/0011-production-orders-own-routing-snapshots.md)).

Die Zieltermine treiben sowohl die terminbasierten Prioritätsregeln (EDD, Critical
Ratio) als auch jede Verspätungskennzahl.

## 5. Belegungsplanung

`DispatchScheduler` macht aus einer **Prioritätsreihenfolge der Aufträge** (einer
Permutation) mit einer einfachen List-Scheduling-Schleife einen konkreten Plan:

```
für jeden Arbeitsplatz: slotFreeAt[platz] = 0        // eine Uhr je Parallelplatz
für jeden Auftrag in Prioritätsreihenfolge:
    jobReadyAt = job.ReleaseSeconds
    für jeden Schritt des Auftrags (in Reihenfolge):
        platz  = der Platz, dessen Belegung AM FRÜHESTEN FERTIG wird    // siehe §6a
        start  = der erste Zeitpunkt ≥ max(jobReadyAt, slotFreeAt[platz]),
                 an dem der Kalender Rüstzeit + Dauer aufnehmen kann
        end    = start + Rüstzeit + step.DurationSeconds + Unterbrechungen
        slotFreeAt[platz] = end
        jobReadyAt        = end
```

Ohne Kalender und ohne Rüstmatrix fallen die beiden mittleren Zeilen auf
`start = max(jobReadyAt, slotFreeAt[platz])` und `end = start + Dauer` zusammen —
der klassische List-Scheduler. Die allgemeine Form oben ist das, was §6a ergänzt.

Zwei Invarianten gelten per Konstruktion und machen jede Ausgabe **zulässig**:

- **Reihenfolge** — der `start` eines Schritts ist `≥ jobReadyAt`, dem Ende des
  vorigen Schritts;
- **Kapazität** — die Uhr jedes Platzes ist streng seriell, ein Arbeitsplatz führt
  also nie mehr als `ParallelCapacity` Arbeitsgänge gleichzeitig aus.

Der Planer ist eine reine Funktion von `(context, order)` — kein Zufall, kein
geteilter Zustand — und damit trivial reproduzierbar.

**Prioritätsregeln** (`PriorityOrdering`) erzeugen die *anfängliche* Reihenfolge,
indem sie Aufträge nach einem Schlüssel sortieren, mit der Auftragsnummer als
deterministischem Gleichstandsbrecher:

| Regel | Schlüssel (aufsteigend = höhere Priorität) |
| --- | --- |
| FIFO | Freigabezeit |
| SPT — kürzeste Bearbeitungszeit | `P` |
| LPT — längste Bearbeitungszeit | `−P` |
| EDD — frühester Termin | `Termin` |
| CR — Critical Ratio | `Termin / P` (am Horizont ausgewertet) |
| WSPT — gewichtete kürzeste Bearbeitungszeit | `P / Gewicht` |

### Die Regeln sind nicht unabhängig von der Terminregel

Bei Freigabe aller Aufträge zur Sekunde 0 und `P` als Gesamtbearbeitungszeit:

| Terminregel | Setzt | Also |
| --- | --- | --- |
| **TWK** | `Termin = f · P` | streng steigend in `P`, also **EDD ≡ SPT**; `CR = Termin/P = f` ist konstant, also **CR ≡ FIFO** |
| **SLK** | `Termin = P + s` | **EDD ≡ SPT**; `CR = (P+s)/P` fällt in `P`, also **CR ≡ LPT** |
| **CON** | `Termin = c` | alle Termine gleich, also **EDD ≡ FIFO**; `CR = c/P` fällt in `P`, also **CR ≡ LPT** |
| **NOP** | `Termin = t · n` | an der Arbeitsgangzahl statt am Arbeitsinhalt — die einzige Regel, die alle sechs entkoppelt |

Auf den vorbelegten TWK-Terminen ergeben die sechs Prioritätsregeln also **vier**
verschiedene Pläne. Das ist eine Eigenschaft der Formeln und kein Fehler — wer aber
eine Auswahl ändert und nichts sieht, hat eine Erklärung verdient. Deshalb
berechnet `PriorityOrdering.EquivalentRules` den Zusammenfall aus den Reihenfolgen
selbst (nie aus einer festen Tabelle, die vom Code abweichen könnte), und die Seite
zeigt ihn unter der Auswahl. `RuleEquivalenceTests` fixiert jede Identität oben;
siehe [ADR 0009](adr/0009-report-rule-equivalences.md).

### Der Zusammenfall hängt an gleichen Freigabeterminen

Alles oben setzt voraus, dass jeder Auftrag zur Sekunde 0 freigegeben wird — das
galt, solange die App Arbeitspläne plante, die keinen Freigabetermin tragen.
Fertigungsaufträge tragen echte, und eine gestaffelte Freigabe bricht die meisten
Identitäten: Bei `Termin = release + f·P` ist das Critical Ratio
`Termin/P = release/P + f` nicht mehr konstant, CR sortiert also nicht mehr wie
FIFO.

Auf den aktuellen Beispieldaten tritt überhaupt kein Zusammenfall auf. Das ist ein
Ergebnis und kein Versäumnis: Erst echte Freigabetermine haben aus sechs
Prioritätsregeln wirklich sechs Regeln gemacht. Die Meldung bleibt, weil der
Zusammenfall in dem Moment zurückkehrt, in dem sich Aufträge einen Freigabetermin
teilen — und genau deshalb wird er aus den Aufträgen berechnet.

## 6. Multi-Start und lokale Suche

Ein einzelner Greedy-Durchlauf ist selten optimal, deshalb umhüllt
`SchedulingEngine` den Planer mit einer kleinen, durchschaubaren Metaheuristik im
Stil von GRASP:

1. **Neustart 0** beginnt bei der reinen Regelreihenfolge, das Ergebnis ist also
   *nie schlechter als die Prioritätsregel allein*.
2. **Neustarts 1…N−1** mischen diese Reihenfolge mit einem Strom aus
   `(Seed, runIndex)`. Mehr Neustarts können nur helfen.
3. **Jeder Neustart** läuft anschließend in einem `LocalSearch`-Abstieg über die
   **Insertion**-Nachbarschaft (or-opt): einen Auftrag entnehmen und an jeder
   anderen Position wieder einfügen — `n·(n−1)` Nachbarn je Durchgang. Der
   Amtsinhaber wird nie durch etwas Schlechteres ersetzt, der fertige Plan ist also
   garantiert `≤` der Regelreihenfolge.

Weil die Suche die **Prioritätsreihenfolge** verändert (nicht die gesetzten
Arbeitsgänge) und den Planer neu laufen lässt, ist jeder betrachtete Kandidat ein
gültiger Plan.

### Welchen verbessernden Nachbarn man übernimmt

„Den besten strikt verbessernden Nachbarn im Durchgang“ (steilster Abstieg) ist eine
von drei Antworten, und es ist nicht mehr die Vorbelegung. `LocalSearchAcceptance`
bietet:

| Regel | Was ein Durchgang tut |
| --- | --- |
| `SteepestDescent` | jeden Nachbarn bewerten, den besten übernehmen |
| `FirstImprovement` | den ersten verbessernden Nachbarn übernehmen und von dort weitergehen |
| `BestInsertion` *(Vorbelegung)* | jeden Auftrag der Reihe nach an seine beste Position setzen |

Über 5 Größen × 5 Instanzen × 3 Budgets bei gleicher Kandidatenzahl gemessen, ist
`BestInsertion` im Mittel dieser fünfzehn Zeilen **6,8 %** besser als der steilste
Abstieg und `FirstImprovement` 5,6 %. Bei 50 Aufträgen gewinnt der steilste Abstieg
jede Zeile; das ist eine Ausprägung einer Familie und steht als Messwert da, statt
weggeredet zu werden. Die Tabellen und der Befehl, der sie erzeugt, stehen in
[ADR 0022](adr/0022-local-search-acceptance.md).

Auch das Budget muss etwas einbringen, und das tut es jetzt: Die zehnfache
Kandidatenzahl bewegt den steilsten Abstieg um 0,9 % und die neue Vorbelegung um
4,6 %.

### Warum Insertion und keine Vertauschung benachbarter Aufträge

Die ursprüngliche Umsetzung vertauschte benachbarte Aufträge und übernahm die erste
Verbesserung. Sie blieb nach 7 bis 16 ihrer 2000 Nachbarn stecken, und das war der
Hinweis: Eine Vertauschung bewegt einen Auftrag je Verbesserungsschritt um eine
Position, ein Auftrag, der zehn Plätze weiter vorn hingehört, ist also nur
erreichbar, wenn auch alle zehn Zwischenpositionen besser sind. Bei einer
Verspätungszielfunktion sind sie das in der Regel nicht.

Gegen die vollständige Aufzählung aller `n!` **Auftragsreihenfolgen**, 20 zufällige
Instanzen mit 8 Aufträgen:

| | mittlerer Abstand zur besten Auftragsreihenfolge | schlechteste | getroffen |
| --- | ---: | ---: | ---: |
| Prioritätsregel allein | 72,5 % | 127,5 % | 0/20 |
| Vertauschung + 8 Neustarts | 27,3 % | 62,8 % | 0/20 |
| **Insertion + 8 Neustarts** | **0,2 %** | **3,0 %** | **19/20** |

Die Spaltenüberschrift bitte genau lesen: Das ist der Abstand zur **besten
Reihenfolge, die der Dispatcher bekommen kann** — der Obergrenze der Suche selbst,
nicht dem Optimum des Planungsproblems. §6c misst den Abstand zwischen beiden, und
er ist groß. Siehe [ADR 0008](adr/0008-insertion-neighbourhood.md).

Eine Folge, die man kennen sollte: Eine gute Suche macht die *Ausgangsregel*
deutlich unwichtiger. Verschiedene Prioritätsregeln laufen auf denselben Plan
hinaus, sofern der Optimierer nicht abgeschaltet ist (Multi-Start = 1, lokale
Suche = 0).

> **Warum die Beispieldaten sieben Aufträge haben.** Bei zwei Aufträgen ist die
> Suche praktisch erschöpfend, jede Prioritätsregel und jeder Seed laufen also auf
> dasselbe Optimum hinaus — eine Änderung *sieht* aus, als bewirke sie nichts. Mit
> sieben Aufträgen, die um dieselben Maschinen konkurrieren, ist die Suche nicht
> mehr erschöpfend, Regel und Seed ändern den Plan also sichtbar. Um die *rohe*
> Wirkung einer Regel zu sehen, setzt man **Multi-Start = 1** und **lokale
> Suche = 0**.

## 6a. Kalender und Rüstzeiten

Beides ist optional und beides ist standardmäßig ohne Wirkung, eine Instanz, die
keines von beiden setzt, verhält sich also genau wie vor ihrer Einführung.

### Verfügbarkeitsfenster

Ein Arbeitsplatz darf Fenster erklären, in denen er verfügbar ist, dazu die Länge
der Periode, über die sie sich wiederholen — `[08:00, 16:00)` mit einer Periode von
24 Stunden ist eine Tagschicht.

Kalender wiederholen sich mit Absicht. Eine endliche Fensterliste läuft entweder
mitten im Plan aus oder zwingt den Aufrufer, ein Jahr davon zu materialisieren;
eine Periode macht den Kalender total, ohne beides. Die Fenster liegen auf derselben
abstrakten Arbeitszeitachse wie alles andere, es gelangt also weder eine Zeitzone
noch eine Sommerzeitregel in den Kern.

Drei Verfeinerungen kamen mit der Arbeitszeit-Bibliothek
([ADR 0012](adr/0012-working-time-as-capacity.md)): Eine **Phase** verschiebt das
Muster, sodass ein Plan montags um 06:00 beginnen darf statt am Periodenursprung;
**Sperrzeiten** sind datierte geschlossene Intervalle mit einer Kennzeichnung (ein
Feiertag, eine Abwesenheit, die Sonntagsruhe), die nie überbrückt werden; und eine
**überbrückbare Lücke** erlaubt es einem Arbeitsgang, über eine kurze Lücke — eine
Pause — zu unterbrechen und danach fortzusetzen, was das Ergebnis als unterbrochene
Sekunden ausweist. Die Auslastung wird gegen die offene Zeit gemessen, nicht gegen
die Durchlaufzeit.

Ein Arbeitsgang muss **vollständig in ein Fenster** passen (oder in eine Folge von
Fenstern, die durch überbrückbare Lücken verbunden sind) — eine Unterbrechung über
etwas Längeres gibt es nicht. Geprüft wird das beim Bau des `SchedulingContext` und
nicht während der Einplanung: Die Suche bewertet Tausende von Kandidaten, und eine
Ausnahme aus dieser Schleife würde den ganzen Lauf abbrechen, statt ein
Eingabeproblem zu melden, das der Aufrufer beheben kann.

### Reihenfolgeabhängige Rüstzeit

Jeder Schritt gehört zu einer *Familie*, und ein Arbeitsplatz darf erklären, was der
Wechsel zwischen Familien kostet. Dieselbe Familie kostet nichts; ein nicht
erklärter Übergang kostet nichts; der erste Arbeitsgang auf einem Platz kostet
nichts.

Das macht die Reihenfolge der Arbeit an einer Maschine über die Warteschlange hinaus
bedeutsam — alle Stahlteile zusammen und danach alle Aluminiumteile schlägt den
Wechselbetrieb. Bei zwei Stunden Rüstzeit kosten vier abwechselnde Aufträge sechs
Stunden Rüsten gegenüber zwei Stunden, wenn man sie bündelt.

Es ändert auch die Platzwahl. Der Planer wählt die Belegung, die **am frühesten
fertig wird**, nicht den Platz, der am frühesten frei ist: Ein Platz, der später
frei wird, aber diese Familie schon gefahren hat, kann früher fertig sein als einer,
der jetzt frei ist und erst gerüstet werden muss.

## 6b. Vollständige Aufzählung der Auftragsreihenfolgen

`ExactDispatchOrderOptimizer` bewertet alle `n!` Auftragsreihenfolgen, bis neun
Aufträge (362 880 Einplanungen, etwa eine Sekunde).

Was das beweist, muss man genau sagen. Es ist exakt **innerhalb des
Dispatch-Order-Modells**: Von jeder Reihenfolge, die der Dispatcher bekommen kann,
liefert es die beste. Es ist *kein* allgemeiner Optimalitätsbeweis für das Job-Shop-
Problem — der Dispatcher setzt jeden Auftrag gierig und füllt Lücken nie nachträglich,
außerhalb dieses Modells gibt es also bessere Pläne. Auf einer Instanz mit zwei
Aufträgen und drei Arbeitsplätzen lautet die Antwort **60 Sekunden, wo das Optimum
40 ist**, und
`ExactSolverTests.The_counterexample_that_costs_the_permutation_optimiser_fifty_percent`
fixiert genau dieses Verhältnis.

Die Klasse bleibt, weil drei übereinstimmende Umsetzungen ein Beleg sind und eine
Umsetzung, die mit sich selbst übereinstimmt, keiner: Sie ist die Zweitmeinung zur
*Suche*, während §6c die Instanz für das *Optimum* ist.

## 6c. Das Optimum, bewiesen

`WorkPlanStudio.Scheduling.Exact` ist ein **Branch-and-Bound über den disjunktiven
Graphen**, das das Planungsproblem löst und nicht das Reihenfolgeproblem. Es teilt
keine Zeile Code mit dem Dispatcher: eigene Belegungsregel, eigene
Relaxationsschranken (Jacksons präemptiver Plan, Auftragsketten), eigene
Propagierung (Zeitfensterverschärfung, Overload, Edge Finding, Not-First/Not-Last)
und eigene Tiefensuche. Es beachtet das vollständige Modell — Freigabetermine,
Parallelkapazität, reihenfolgeabhängige Rüstzeiten, wiederkehrende Kalender mit
Phase und überbrückbaren Lücken, Sperrzeiten, jede Terminregel und denselben
gewichteten Strafwert — und wo es nicht antworten kann, **verweigert** es nach
Größe, statt zu nähern.

Es gibt dieselbe Instanz zusätzlich als CPLEX-LP-Datei aus (`MilpModelWriter`),
sodass ein Löser die Antwort prüfen kann, der keinem dieser Codeteile traut. Für die
Zahlen unten wurde kein externer Löser ausgeführt; das Rezept steht in der ADR und
ist als ungetestet gekennzeichnet.

Auf einem festen, eingecheckten Satz von zwanzig Instanzen über 2–8 Aufträge, 1–5
Arbeitsplätze, 1–3 Plätze, gestaffelte Freigaben, vier Terminregeln,
Rüstzeitfamilien, eine Tagschicht, Schichten mit überbrückbarer Pause und einen
zweitägigen Betriebsstillstand:

| | Wert |
| --- | ---: |
| vom Löser als optimal bewiesene Instanzen | **20 von 20** (1,26 s für den Satz) |
| Engine trifft die **beste Auftragsreihenfolge** | **18 von 20**, mittlerer Abstand **0,27 %** |
| Engine trifft das **wahre Optimum** | **7 von 20** |
| Abstand zum wahren Optimum | Median **5,34 %**, Mittel **327 %**, schlechtester **6 091 %** |
| Abstand der besten Auftragsreihenfolge zum Optimum | Mittel **326,76 %** |

Zwei ehrliche Lesarten dieser Tabelle.

**Die Suche ist nah an ihrer Obergrenze; die Obergrenze ist das Modell.** 0,27 % ist
nah genug an den 0,2 % aus §6, dass es fast sicher dieselbe Größe ist. Die Engine
findet auf achtzehn von zwanzig Instanzen die beste Reihenfolge, die ihr Dispatcher
bekommen kann. Alles Übrige im Abstand ist das Dispatch-Order-Modell — ein ganzer
Auftrag nach dem anderen, ohne Lücken nachträglich zu füllen — und eine bessere
Metaheuristik würde daran nichts ändern.

**Der Mittelwert wird von einer Instanz getragen, und der Median daneben ist die
Zahl, die man lesen sollte.** Die Zielfunktion berechnet pauschal hundert Punkte je
verspätetem Auftrag. Bei `wide-5x4` hat das Optimum überhaupt keinen verspäteten
Auftrag und die beste Auftragsreihenfolge zwei, das Verhältnis ist also 61. Das ist
ein echter Unterschied — zwei verspätete Aufträge statt keiner —, aber nur den
Mittelwert zu nennen wäre eine eigene Art von Unehrlichkeit.

```bash
dotnet run -c Release --project tools/WorkPlanStudio.Scheduling.Scenarios -- exact
```

erzeugt die vollständige Tabelle je Instanz neu, und `OptimalityStudyTests` fixiert
jede Zahl darin auf sechs Nachkommastellen, prüft, dass alle zwanzig als optimal
bewiesenen Pläne die unabhängige Zulässigkeitsprüfung bestehen, und prüft, dass der
Instanzsatz den Funktionsumfang weiterhin abdeckt, statt in zwanzig Kopien einer
Ausprägung zusammenzufallen. Der vollständige Datensatz ist
[ADR 0015](adr/0015-exact-solver.md).

**Wo es aufhört.** Einundzwanzig Arbeitsgänge sind eine Kante und keine Steigung:
Bis achtzehn wird jede untersuchte Instanz in unter einer Sekunde bewiesen; bei
einundzwanzig und vierundzwanzig sind drei von fünf in Millisekunden bewiesen und
die übrigen zwei auch in einer Minute nicht. Nicht die Größe ist der Prädiktor —
fünfunddreißig Arbeitsgänge wurden in 406 Knoten bewiesen und einundzwanzig nach
zweihundert Millionen nicht. Der Unterschied liegt darin, wie viel vom Optimum die
Relaxation schon kennt.

Die Anwendung kann den Beweis für den gerade angezeigten Plan anfordern, unter einer
Zeitschranke, und berichtet nur, was bewiesen wurde: „optimal“, oder den Abstand zu
einem gefundenen besseren Plan, oder die bewiesene untere Schranke — „höchstens
*n* % über dem Bestmöglichen“ —, wenn die Zeit nicht reichte.

## 7. Bewertung

`ScheduleEvaluator` fasst den Plan zu Kennzahlen und einem einzigen Strafwert
zusammen:

- **Durchlaufzeit (Makespan)** — wann der letzte Arbeitsgang fertig ist.
- **Verspätung** — je Auftrag `max(0, Fertigstellung − Termin)`; als Summe und als
  Maximum ausgewiesen.
- **Termintreue** — Anteil der Aufträge, die ihren Termin halten.
- **Auslastung** — belegt ÷ (Kapazität × offene Zeit) je Arbeitsplatz, dazu ein
  Mittelwert; geschlossene Zeit (Schichten, Pausen, Sonntage, Feiertage,
  Abwesenheiten) zählt nicht als ungenutzt.
- **Strafwert** (von der Suche minimiert), in Stunden gerechnet, damit die Gewichte
  anschaulich bleiben:

  ```
  Strafwert = MakespanWeight · Durchlaufzeit in Stunden
            + TardinessWeight · Gesamtverspätung in Stunden
            + LatePenalty     · Anzahl verspäteter Aufträge
  ```

  Mit den Vorbelegungen dominiert die Anzahl verspäteter Aufträge, dann die
  Gesamtverspätung, dann die Durchlaufzeit — also *zuerst die Termine halten, dann
  schnell fertig werden*.

## 8. Determinismus

- Die gesamte Belegungsarithmetik ist ganzzahlig.
- Der Zufall ist ein PRNG mit festem Algorithmus (xorshift64\*,
  `DeterministicRandom`) und nicht `System.Random`, dessen Algorithmus über
  .NET-Versionen nicht zugesichert ist. Jeder Neustart erhält seinen eigenen Strom
  aus `(Seed, runIndex)`, und die Engine läuft einsträngig.
- Die Prioritätsreihenfolge ist kanonisch (nach Schlüssel, dann Auftragsnummer
  sortiert), der Plan ist also sogar **unabhängig von der Reihenfolge, in der die
  Aufträge übergeben werden** — und eine doppelte Auftragsnummer wird jetzt namentlich
  abgewiesen statt geduldet, was aus diesem Satz erst eine wahre Aussage macht.
- Den Lauf über Browser-Durchläufe zu zerlegen ändert nichts:
  `ScheduleRunnerTests.Slicing_the_run_does_not_change_the_schedule_it_produces`
  prüft, dass zerlegter und durchlaufender Lauf in Plan-Signatur, Strafwert und
  Schrittzahl übereinstimmen.

Diese Eigenschaften werden unmittelbar von Tests geprüft (Golden-PRNG-Werte,
identischer Plan bei wiederholtem Lauf, identischer Plan bei vertauschten Eingaben).

## 8a. Was die Engine zurückweist

Eine Heuristik, die Unsinn stillschweigend annimmt, macht daraus einen plausiblen
Plan, und das ist schlimmer als eine Ausnahme. `SchedulingContext` prüft deshalb beim
Bau und wirft, unter Nennung des betroffenen Auftrags oder Arbeitsplatzes:

- doppelte Auftrags- oder Arbeitsplatznummern;
- einen Auftrag ohne Zieltermin — der galt früher stillschweigend als „nie
  verspätet“;
- eine negative Freigabe, ein Gewicht, das nicht endlich und positiv ist, eine
  Referenz über 80 Zeichen;
- einen Rüstzeiteintrag von einer Familie auf sich selbst, der früher verworfen
  wurde;
- einen Schritt über `MaxStepDurationSeconds` oder einen Horizont über
  `MaxHorizonSeconds`, beide mit `checked`-Arithmetik dahinter, denn eine
  unbegrenzte Dauer lief früher in einen *negativen* Strafwert über, den die Suche
  dann bereitwillig minimierte.

Der Kontext **kopiert** außerdem jede übergebene Sammlung, damit ein Aufrufer, der
seine Liste danach ändert, einen bereits geprüften Kontext nicht mehr verändern kann.
Alles, was externe Daten abbildet — der `ScheduleMapper` der App, der CSV-Import —,
muss diese Bedingungen erfüllen, und die App macht aus jeder davon eine Ablehnung je
Auftrag mit einem Satz statt eines Stack-Trace.

## 9. Wie die App sie nutzt

`ProductionScheduleService` (in der Blazor-App) ist die Grenze:

1. lädt die **freigegebenen Fertigungsaufträge** und aktiven Arbeitsplätze aus der
   Browser-Datenbank, jeden Auftrag mit seinem eingefrorenen Arbeitsplan;
2. bildet Arbeitsgänge über den `ScheduleMapper` auf Schritte ab und wandelt
   Dezimalminuten mit kaufmännischer Rundung in ganze Sekunden — die einzige Stelle
   im Repository, an der diese Umrechnung stattfindet;
3. lehnt einen Auftrag **vollständig** ab, wenn einer seiner Arbeitsgänge nicht
   abbildbar ist, mit einem stabilen Grund, den die Seite lokalisiert: ein
   stillgelegter oder unbekannter Arbeitsplatz, ein Arbeitsgang länger als das
   längste Schichtfenster des Platzes, ein Arbeitsgang länger als die Dauerschranke
   der Engine, oder ein Arbeitsplatz, den die Arbeitszeitregeln ganz geschlossen
   haben;
4. baut über `ShopCalendar` je Arbeitsplatz einen `MachineCalendar` aus den
   Betriebsregeln sowie Schichtmodell und Abwesenheiten dieses Platzes;
5. führt die Engine über `IScheduleRunner` aus, der die Multi-Start-Schleife zerlegt,
   damit der Browser weiterzeichnet und die Schaltfläche „Abbrechen“ funktioniert
   ([ADR 0019](adr/0019-off-thread-scheduling.md));
6. projiziert das Ergebnis in die Gantt-Zeilen mit ihrer geschlossenen Zeit, die
   Auftragstabelle und die Kennzahlenkarten — und auf Wunsch in ein PDF, eine
   Excel-Arbeitsmappe oder eine CSV-Datei
   ([ADR 0017](adr/0017-in-browser-export.md)).

## 10. Umfang und mögliche Erweiterungen

Bewusst außerhalb des Umfangs gelassen, um einfach und beweisbar korrekt zu bleiben:

- **Rückwärtsplanung (terminverankert)** — unter geteilter endlicher Kapazität
  braucht sie einen zweiten Planer und kann unzulässige Pläne erzeugen;
  Vorwärtsplanung mit terminbasierten *Prioritätsregeln* holt den meisten Nutzen.
- **Maschinenanzahl je Arbeitsplatz in der App** — die App bildet jeden Arbeitsplatz
  auf einen Platz ab, obwohl die Engine `ParallelCapacity > 1` unterstützt (und die
  Tests, der exakte Löser und die Studie aus ADR 0015 es nutzen).
- **Lücken nachträglich füllen** — und §6c ist jetzt die Messung, die sagt, was das
  wert wäre. Der Dispatcher setzt die Arbeitsgänge eines Auftrags in Reihenfolge und
  schiebt spätere Arbeit nie in ein früheres Leerfenster, und genau das ist der
  gesamte Abstand von 327 % im Mittel und 5,3 % im Median zwischen der besten
  Auftragsreihenfolge und dem Optimum. Es ist zugleich die Eigenschaft, auf der die
  Suche beruht — „die Reihenfolge bestimmt den Plan“ ist es, was jeden Kandidaten
  per Konstruktion zulässig macht —, also ein Umbau und kein Flicken.
- **Losteilung**, ein **kumulativer Edge Finder**, damit ein Arbeitsplatz mit mehreren
  Plätzen mehr bekommt als die Overload-Prüfung (deshalb kostet eine Instanz mit
  zwölf Arbeitsgängen in der Studie 174 562 Knoten), und **rüstzeitbewusste
  Relaxationsschranken**. Alle drei sind in [ADR 0015](adr/0015-exact-solver.md) als
  die nächsten Schritte benannt.

---

*Die Unit-Tests in
[`tests/WorkPlanStudio.Scheduling.Tests`](../tests/WorkPlanStudio.Scheduling.Tests)
sind die ausführbare Spezifikation jeder hier beschriebenen Zusicherung.*
