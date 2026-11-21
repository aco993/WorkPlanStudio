# Erwischt

[English](CAUGHT.md) · **Deutsch**

Dieses Projekt entstand mit KI-Unterstützung, und der
[Hinweis im README](../README.de.md#hinweis-zur-ki-gestützten-entwicklung) sagt das.
Entscheidend ist daran nicht, wie viel erzeugt wurde, sondern was geschah, als das
Erzeugte — oder der Autor — falsch lag. Diese Seite ist das Protokoll: jeder Eintrag ist
ein echter Fehler oder eine falsche Behauptung, was er gekostet hätte, **was ihn gefunden
hat**, und wo Korrektur und Beleg stehen. Nichts davon wurde gefunden, indem jemand einen
Diff las und nickte.

Auf das Muster kommt es an. Fast jeder Fund kam von einem Instrument, das das Gegenteil
beweisen sollte: einer Browserprüfung, die zeigen sollte, dass eine Änderung sicher ist,
einer Messung, die eine Zahl bestätigen sollte, einer CI-Untergrenze, die bemerken soll,
dass eine Suite schrumpft.

## Gefunden von einer Prüfung, die zeigen sollte, dass es funktioniert

| Was falsch war | Was es gekostet hätte | Gefunden von | Wo |
| --- | --- | --- | --- |
| Der Hash-Schritt des Deployments hashte einen **Kommentar**, der `<script type="importmap">` zitierte, zusammen mit der echten Import-Map | der Browser verweigert die Map; die veröffentlichte Seite startet nie | der Browsercheck nach dem Festlegen | [ADR 0031](adr/0031-pin-inline-scripts-by-hash.md), [#110](https://github.com/aco993/WorkPlanStudio/pull/110) |
| `System.Private.Xml` nachladen (436 KB, nie ausgeführt) | die App kann ihre Datenbank nicht öffnen: die VTable von `SqliteConnection` braucht `DataTable`, das braucht XML | ein Browsertest, der beweisen sollte, dass die Assembly nie angefordert wird | [PERFORMANCE.de.md](PERFORMANCE.de.md#vier-hebel-gemessen-keiner-übernommen) |
| Der Service Worker cachte in Produktion **nichts**, lokal alles | „der zweite Besuch ist kostenlos“ falsch auf der einzigen Seite, die jemand sieht | Prüfen der veröffentlichten Seite statt des Builds | [ADR 0027](adr/0027-pay-the-cold-start-once.md), [#85](https://github.com/aco993/WorkPlanStudio/pull/85) |
| Die Budget-Prüfung bestand, wenn jeder Budget-Test **übersprungen** wurde | ein Stolperdraht, der nie auslösen kann | Lesen des Laufs, der ihn hätte auslösen sollen | [#94](https://github.com/aco993/WorkPlanStudio/pull/94) |

## Gefunden von CI, nicht von einem Menschen

| Was falsch war | Was es gekostet hätte | Gefunden von | Wo |
| --- | --- | --- | --- |
| Das Aufteilen der Planungsseite ([#97](https://github.com/aco993/WorkPlanStudio/pull/97)) verschob das Zurücksetzen des Chats *hinter* den Lauf: für ein `await` waren die Vorschläge gegen den alten Kontext klickbar | eine Frage, die mit dem Ende eines Laufs zusammenfällt, verschwindet still | die Chat-Tests, die in einer Nacht auf drei unabhängigen Pull Requests scheiterten | [#111](https://github.com/aco993/WorkPlanStudio/pull/111) |
| Dependabot bündelte `Microsoft.AspNetCore.Authorization` **11.0.0** (ein .NET-11-Paket) mit zehn 10.0.x-Patches | die API scheitert beim Start | alle 76 gehosteten API-Tests rot auf dem Abhängigkeits-PR | [#112](https://github.com/aco993/WorkPlanStudio/pull/112) |
| Der Rollenschalter hieß „Working as“, zeigte aber „Planner“ — WCAG 2.5.3, Stufe A, auf jeder Route | Sprachsteuerung kann das Element nicht benennen | ein neueres axe in einem Dependabot-PR, das 38 von 76 Browsertests scheitern ließ | [#83](https://github.com/aco993/WorkPlanStudio/pull/83) |
| Die Korrektur für NVDAs „Working asPlanner“ war ein `aria-label` — das den sichtbaren Text nicht mehr enthielt (Initiale des Avatars plus Rolle) | WCAG 2.5.3 erneut verletzt, auf jeder Route, durch die Korrektur für einen Screenreader | axes `label-content-name-mismatch` in der Browser-Suite, 38 Scans rot | [#119](https://github.com/aco993/WorkPlanStudio/pull/119) |

## Gefunden durch Messen statt Annehmen

| Was falsch war | Was es gekostet hätte | Gefunden von | Wo |
| --- | --- | --- | --- |
| Die einzige Layoutverschiebung der App. **Erste Vermutung falsch:** nicht der späte Speichern-Knopf, sondern `EditableArea`, das nichts renderte, solange die Rolle gelesen wurde | 0,05 CLS und 6,1 s LCP auf `/working-time` | ein zweiter Lighthouse-Lauf, der sich nicht bewegte, dann eine Layout-Spur Frame für Frame | [ADR 0030](adr/0030-render-the-first-paint-at-build-time.md), [#105](https://github.com/aco993/WorkPlanStudio/pull/105) |
| Ein Beweisurteil überlebte den Plan, den es beurteilte | „Optimal bewiesen“ neben einem Plan, über den niemand etwas bewiesen hatte | ein beim Herauslösen der Komponente geschriebener Test, der gegen die alte Seite scheitert | [#97](https://github.com/aco993/WorkPlanStudio/pull/97) |
| Das Kernergebnis der Engine war gegen den **falschen Maßstab** gemessen (beste Einlastungsreihenfolge statt Optimum) | eine Aussage zur Optimalität, die der Code nicht trug | ein unabhängiger exakter Löser, geschrieben, um sie zu prüfen | [ADR 0015](adr/0015-exact-solver.md) |
| Nur 43,8 % des Texts der Übersicht waren auf dem Telefon 12 px oder größer | unlesbare Beschriftungen für die meisten, die einen Portfolio-Link öffnen | der erste mobile Lighthouse-Lauf | [#113](https://github.com/aco993/WorkPlanStudio/pull/113) |
| 51 unbenannte Bilder, „Bearbeiten, Bearbeiten, Bearbeiten“, ein gesprochenes „plus“ — nichts davon ein axe-Verstoß | Lärm für alle, die einen Screenreader nutzen | Lesen des Barrierefreiheitsbaums Route für Route | [#117](https://github.com/aco993/WorkPlanStudio/pull/117) |
| Was NVDA sagte, das der Baum nicht zeigte: jeder Download-Prozentwert angesagt, „Working asPlanner“, jede Symbolschaltfläche doppelt | Lärm für Screenreader-Nutzer während des gesamten ersten Besuchs | NVDA 2026.3 selbst, sein Debug-Protokoll mit jeder Äußerung | [#119](https://github.com/aco993/WorkPlanStudio/pull/119) |
| Zeitzonendaten abschalten sparte 75 KB | Export-Zeitstempel und „heute“ in UTC: kurz vor Mitternacht das Datum von gestern | Durchdenken, was `TimeZoneInfo.Local` liest, vor dem Ausliefern | [PERFORMANCE.de.md](PERFORMANCE.de.md#vier-hebel-gemessen-keiner-übernommen) |

## Gefunden in den eigenen Instrumenten des Autors

Die Werkzeuge, die Fehler finden, haben selbst Fehler, und die werden genauso festgehalten.

| Was falsch war | Gefunden von |
| --- | --- |
| Der Zielgrößen-Test maß eine 13-px-Checkbox statt des Labels, das den Klick annimmt, und meldete fünf Fehler, die es nicht gab | Vergleich seiner Ausgabe mit der Seite |
| Eine Tab-Reihenfolge-Prüfung brach beim ersten wiederholten Namen („Bearbeiten“) ab und meldete fünf Stationen statt sechsundzwanzig | die unplausibel niedrigen Zahlen |
| Die Testzahlen der Dokumentation waren verrutscht (867 behauptet, 986 gelaufen), und die CI-Untergrenzen lagen bis zu 8 % unter der Wahrheit | jede Suite laufen lassen, bevor eine Zahl geschrieben wird ([#102](https://github.com/aco993/WorkPlanStudio/pull/102)) |
| Ein neuer Gantt-Test klickte den Zoom, während der erste Lauf der Seite noch endete; ab und zu ging der Klick verloren | main wurde beim Merge rot, der ihn einführte, nachdem er im Pull Request bestanden hatte ([#122](https://github.com/aco993/WorkPlanStudio/pull/122)) |

## Wie das zu lesen ist

Jeder Eintrag nennt seinen Beleg: einen Pull Request, ein ADR oder eine Messung in der
Dokumentation, jeweils mit dem Test oder Lauf, der es zeigt. Die Regel des Projekts lautet,
dass keine Zahl genannt wird, die nicht gemessen wurde ([AGENTS.md](../AGENTS.md), goldene
Regel 8); diese Seite hält denselben Maßstab für Fehler. Wird der nächste gefunden, kommt
er hierher.
