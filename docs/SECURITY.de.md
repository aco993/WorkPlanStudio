# Sicherheitslage

[English](SECURITY.md) · **Deutsch**

WorkPlan Studio wird in zwei Gestalten ausgeliefert, und sie haben verschiedene
Sicherheitsgeschichten.

- **Die veröffentlichte Demo** — <https://aco993.github.io/WorkPlanStudio/> — ist eine
  statische Seite ohne Server, ohne Konten und ohne gemeinsamen Speicher. Sie hat keine
  Berechtigungsgrenze und keinen vertrauenswürdigen Speicher, und dieses Dokument sagt
  das unten genauer.
- **Ein optionales Backend** (`src/WorkPlanStudio.Api`) ist vorhanden und hat echte
  Konten, echtes Passwort-Hashing und echte Tokenbehandlung. Es ist **aus, solange es
  nicht konfiguriert ist**, und die veröffentlichte Demo konfiguriert es nicht.

Nichts im Folgenden behauptet, dass die Demo Daten schützt. Sie tut es nicht.

## Daten- und Vertrauensgrenzen (der statische Build)

- Arbeitspläne, Stammdaten und Einstellungen bleiben im `localStorage` der aktuellen
  Browser-Herkunft.
- `localStorage` ist von jedem JavaScript dieser Herkunft lesbar. Er ist kein Tresor und
  für gemeinsam genutzte Rechner oder wertvolle Zugangsdaten ungeeignet.
- Datenbank-Blöcke gelten beim Start als nicht vertrauenswürdig. Version, Base64-Form,
  SQLite-Header, Mindestgröße, `PRAGMA quick_check` und der erwartete Schemazugriff
  werden geprüft, bevor die Oberfläche freigegeben wird.
- Der Anwendungscode setzt feste, von EF erzeugte Abfragen ab; er nimmt kein SQL vom
  Benutzer entgegen.
- Ein unverträglicher oder beschädigter Block wird nie stillschweigend überschrieben.
  Der Wiederherstellungsbildschirm bietet Export, Import und ein ausdrückliches
  zweistufiges Zurücksetzen — und sowohl Zurücksetzen als auch Import fragen die
  Berechtigungsrichtlinie, denn alle Zeilen zu ersetzen ist ein Schreibvorgang, wie die
  Aufrufstelle auch aussieht.

### Importierte und hochgeladene Inhalte

Der CSV-Import liest eine Datei, die der Besucher ausgewählt hat — die eine Stelle, an
der nicht vertrauenswürdige Bytes in die Anwendung gelangen. Er weist eine Datei über
8 MB an der Tür ab, weist eine Datei mit einem NUL-Byte als „keine Textdatei“ ab,
begrenzt ein Feld auf 64 KB, eine Zeile auf 1 024 Spalten und eine Datei auf 200 000
Datensätze und weist ein nicht geschlossenes Anführungszeichen unter Angabe der Zeile
ab. Werte laufen durch dieselben Validatoren wie die Formulare, die Steuerzeichen in
Bezeichnungen, Nummern und Auftragsnummern ablehnen. Nichts Importiertes wird je
ausgeführt, und der CSV-Export maskiert führende `=`, `+`, `-` und `@`, damit eine
Tabellenkalkulation eine Zelle nicht als Formel liest.

## Rollen sind keine Zugriffskontrolle

Der statische Build bietet drei Rollen — Planer, Meister, Gast — über die
Standard-Autorisierungspipeline von ASP.NET Core (Richtlinien, `AuthorizeView`, eine
Prüfung in jedem schreibenden Dienst, als geschlossene Menge von einem Reflexionstest
durchgesetzt). Die Identität dahinter ist eine in der Oberfläche gewählte Rolle im
`localStorage`; ihr Authentifizierungstyp heißt `demo-persona`. Jeder kann Planer sein,
indem er es wählt, und der Code, der die Richtlinien durchsetzt, läuft im Browser des
Besuchers.

Das führt Autorisierungsverdrahtung und ihre Nahtstelle zu einem echten Identitätsanbieter
vor ([ADR 0013](adr/0013-personas-through-the-real-authorization-pipeline.md)); **es
schützt nichts**.

Diese Nahtstelle ist inzwischen mehr als eine Behauptung. Ist `Api:BaseAddress`
konfiguriert, setzt dieselbe Richtlinientabelle — *dieselbe Quelldatei*, in beide Wirte
kompiliert — ein Server durch, dem die Daten gehören, gegen einen Principal aus einem von
ihm ausgestellten JWT, und der Rollenwechsler verschwindet aus der Oberfläche. Siehe
[ADR 0020](adr/0020-optional-backend-and-real-auth.md). Die veröffentlichte Demo ist
weiterhin der Rollen-Build.

## Das optionale Backend

Wird es betrieben, leistet `src/WorkPlanStudio.Api` Folgendes, und die 88 Integrationstests
üben jeden Punkt gegen eine echte SQLite-Datei aus:

- **Identity** für Benutzerverwaltung und Passwort-Hashing; Konten werden nach einer
  konfigurierten Zahl von Fehlversuchen gesperrt, und eine Sperre schlägt auch das
  richtige Passwort.
- **JWT-Zugriffstokens** (standardmäßig 10 Minuten) mit **Refresh-Tokens** aus 256 Bit
  von `RandomNumberGenerator`, nur als SHA-256 gespeichert, bei jedem Tausch rotiert. Wird
  ein verbrauchter Refresh-Token vorgelegt, werden alle offenen Sitzungen dieses Kontos
  entwertet — Wiederverwendung gilt als Diebstahl, nicht als Wiederholungsversuch.
- Ein unbekanntes Konto antwortet **Byte für Byte wie ein falsches Passwort**, der
  Anmeldeendpunkt zählt also keine Benutzer auf.
- **Der Start verweigert** einen fehlenden, kürzeren als 32 Byte langen oder den
  Beispiel-Signaturschlüssel außerhalb der Entwicklungsumgebung. Das ist Optionsvalidierung,
  aufgelöst bevor die Datenbank angefasst wird, damit der Wirt nicht halb konfiguriert
  startet.
- **Ratenbegrenzung** mit festem Fenster auf den Anmelderouten; die Registrierung ist
  standardmäßig aus und verlangt eingeschaltet zusätzlich einen berechtigten Aufrufer.
- **CORS aus einer konfigurierten Herkunftsliste**, nie `AllowAnyOrigin` mit
  Anmeldeinformationen — und es werden überhaupt keine verwendet, weil das Token in einer
  Bearer-Kopfzeile reist und nicht in einem Cookie.
- **ProblemDetails** (RFC 9457) bei jedem Fehler, eine Begrenzung des Anfragerumpfs auf
  256 KB, Sicherheitskopfzeilen bei jeder Antwort, Antwortkomprimierung mit
  `EnableForHttps = false` (BREACH) sowie `/health` und `/health/ready`.
- Ein mehrstufiges **Dockerfile**, das als Nicht-Root-Benutzer läuft und kein Geheimnis
  einbackt.
- **Optimistische Nebenläufigkeit**: Ein Schreibvorgang ohne Concurrency-Stempel wird
  abgewiesen, ein veralteter ergibt 409 statt eines stillen Überschreibens.

Was es bewusst nicht tut: Mandantenfähigkeit, eine Warteschlange für Offline-Schreibvorgänge,
Konfliktzusammenführung über 409 hinaus oder Abgleich in beide Richtungen. Stammdaten
werden nur in eine Richtung geholt; der Browser schickt seine lokalen Zeilen nie zurück,
und die Oberfläche sagt das, statt „letzter gewinnt“ vorzutäuschen.

## Optionale Modelle (eigener Schlüssel)

Die Kernanwendung, die deterministische Erläuterung und der Chat auf dem Gerät
funktionieren ohne jedes Modell. Ist ein Anbieter aktiviert:

- liegt der Schlüssel im `localStorage` des Browsers, **unter einem Namen je Anbieter**
  (`assistant.key.<Anbieter>`) und getrennt von den übrigen Assistenteneinstellungen, die
  `[JsonIgnore]` auf dem Schlüssel tragen, damit er im Einstellungsblock nicht mitreist.
  Ein gemeinsames Schlüsselfeld für drei Anbieter bedeutete, dass ein Anbieterwechsel den
  für einen Wirt eingegebenen Schlüssel auf einen anderen richtete; das ist behoben;
- **liest der Einstellungsdialog den Schlüssel nie zurück in die Seite.** Das Feld ist beim
  Öffnen leer, und ein leeres Feld bedeutet „den gespeicherten Schlüssel behalten“, das
  Geheimnis gelangt also nicht dorthin zurück, wo jedes Skript der Herkunft es lesen
  könnte. „Diesen Schlüssel vergessen“ ist ein Klick;
- steht das verbleibende Risiko im Dialog selbst, nicht nur hier: Der Speicher dieser
  Herkunft ist von Skripten lesbar, also kein Modell auf einem gemeinsam genutzten Rechner
  aktivieren;
- wird im Assistenten überhaupt nichts protokolliert, und zwei Quelltext-Scans halten das
  so;
- wird nur ein absoluter HTTPS-Endpunkt angenommen; HTTP nur für die Loopback-Entwicklung.
  Benutzerinformationen, Query und Fragment werden abgelehnt, um versehentliches
  Weiterreichen von Zugangsdaten zu vermeiden;
- **werden Modellname ebenso geprüft und maskiert.** Er ist in der Gemini-API ein
  Pfadsegment, ein ungeprüfter Name könnte also das Ziel der Anfrage umschreiben — dasselbe
  Loch, gegen das die Endpunktregeln geschrieben wurden, durch ein anderes Feld wieder
  geöffnet. Er muss jetzt `^[A-Za-z0-9._:@-]{1,100}$` entsprechen und wird an der
  Verwendungsstelle prozentkodiert;
- reist der Schlüssel in der Kopfzeile, die der jeweilige Anbieter erwartet
  (`Authorization: Bearer`, `x-api-key`, `x-goog-api-key`), nie in der URL. Ein
  API-Schlüssel mit einem Steuerzeichen oder einem Nicht-ASCII-Zeichen wird abgelehnt, denn
  `TryAddWithoutValidation` ist genau die Schnittstelle, die ein `\r\n` durchlassen würde;
- gehen die Aufrufe vom Browser zum Anbieter. Anthropic verlangt dafür ausdrücklich die
  Kopfzeile `anthropic-dangerous-direct-browser-access`, die der Client sendet; der Name
  ist die Warnung, und er ist der Grund, warum es der eigene Schlüssel des Benutzers ist
  und nie einer, der mit der App ausgeliefert wird;
- **deckt ein Budget von 20 Sekunden den gesamten Austausch ab**, die Anfrage *und* das
  Lesen des Antwortrumpfs. Früher deckte es nur das Senden ab, sodass ein Rumpf, der nie
  fertig ankam, über das Budget hinaus hing; ein verknüpftes Abbruch-Token steuert jetzt
  beides. Der Abbruch durch den Aufrufer bleibt vom Zeitablauf unterscheidbar;
- wird eine Antwort über **1 MiB** abgewiesen statt gepuffert, und eine zu große
  `Content-Length` wird abgewiesen, bevor ein Byte gelesen wird;
- fällt **jeder** Anbieterfehler auf die Antwort vom Gerät zurück, mit Hinweis. Nicht eine
  Liste von sieben Ausnahmetypen — jeder, auch die fehlerhaften Rümpfe, die früher als
  `NullReferenceException` ankamen;
- werden nur strukturierte Plandaten, das Gespräch und die Antwort vom Gerät gesendet, und
  diese Daten sind **als Daten eingefasst**: in Begrenzer gesetzt, von Markup-Zeichen
  befreit, begrenzt (40 Aufträge, 20 Arbeitsplätze, 12 Arbeitsgänge je Arbeitsplatz,
  16 000 Zeichen), wobei jede Kürzung im Prompt *ausgesprochen* wird, damit eine gekürzte
  Liste nicht als der ganze Betrieb gelesen werden kann, und gefolgt von der Wiederholung,
  dass der eingefasste Bereich Daten sind und nie eine Anweisung.

**Prompt Injection ist gemindert, nicht gelöst.** Was innerhalb der Einfassung steht, ist
Freitext, den jemand in Teilebezeichnungen, Arbeitsplatzbezeichnungen und Auftragsnummern
geschrieben hat. Die App kann über ein Feld, in das ein Mensch alles schreiben kann, nicht
zusichern, dass es keine personenbezogenen Daten enthält; sie kann zusichern, dass das Feld
bereinigt, begrenzt, eingefasst und der Anweisung nie übergeordnet ist. Siehe
[ADR 0025](adr/0025-hostile-input-on-the-model-path.md).

Ein Produktiventwurf würde den Anbieter hinter einen Backend-Proxy setzen, den Schlüssel in
einem serverseitigen Geheimnisspeicher halten, Mandantenberechtigungen durchsetzen und
Protokollierung und Ratenbegrenzung ergänzen.

## Nachverfolgter SQLite-Hinweis — SEC-001, Ausstiegskriterium erfüllt

**Stand 11.09.2026: Der Hinweis greift nicht mehr, und die Unterdrückung ist überflüssig
geworden.**

Zur Vorgeschichte: `Microsoft.EntityFrameworkCore.Sqlite` brachte früher
`SQLitePCLRaw.lib.e_sqlite3` **2.1.11** mit, wofür die NuGet-Prüfung
[GHSA-2m69-gcr7-jv3q / CVE-2025-6965](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)
als hoch einstufte — Speicherkorruption in SQLite vor 3.50.2 bei übermäßig vielen
Aggregatstermen. Das Risiko wurde angenommen statt behoben, mit schriftlicher Begründung
und einem widerlegbaren Ausstiegskriterium, und als einzelner Hinweis in
`Directory.Build.props` unterdrückt, damit jeder *andere* NuGet-Hinweis die Prüfung
weiterhin scheitern lässt.

Die Pakete stehen jetzt bei `Microsoft.EntityFrameworkCore.Sqlite` **10.0.11**, das
`SQLitePCLRaw` **2.1.12** mitbringt. Gemessen an einem Wegwerfprojekt, das EF 10.0.11
referenziert und **überhaupt keine Unterdrückung** trägt:

```
dotnet list package --vulnerable --include-transitive
  → für das angegebene Projekt liegen keine anfälligen Pakete vor
```

Das Ausstiegskriterium von SEC-001 lautete: *„`dotnet list WorkPlanStudio.slnx package
--vulnerable --include-transitive` meldet den Hinweis mit einem unterstützten Paketgraphen
nicht mehr, alle SQLite-, WASM- und E2E-Tests bestehen, und die Unterdrückung wird in
derselben Änderung entfernt.“* Die ersten beiden Punkte sind erfüllt — der Graph ist sauber
und jede Suite ist auf diesem Stand grün. Auch der dritte ist erfüllt: Die Zeile
`NuGetAuditSuppress` ist aus `Directory.Build.props` entfernt. Ein Restore mit vollständig
scharfer NuGet-Prüfung und ohne jede Unterdrückung meldet keinen Hinweis, und die Projektmappe
baut warnungsfrei — was dieselbe Prüfung ist, denn Warnungen sind hier Fehler.

**SEC-001 ist abgeschlossen.** Der Eintrag bleibt stehen: Eine Risikoannahme mit einem
falsifizierbaren Ausstiegskriterium ist nur dann etwas wert, wenn der Ausstieg auch
festgehalten wird, sobald er eintritt.

## Meldung

Geheimnisse oder Exploit-Daten gehören nicht in ein öffentliches Issue. Nutzen Sie die
private Schwachstellenmeldung von GitHub, sofern sie für das Repository aktiviert ist,
sonst wenden Sie sich privat an den Eigentümer. Was im Geltungsbereich liegt und was
dokumentierter Entwurf statt Defekt ist, steht in [SECURITY.md](../SECURITY.md) im
Wurzelverzeichnis.
