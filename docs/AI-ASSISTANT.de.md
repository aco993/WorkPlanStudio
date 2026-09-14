# Der Planungs-Assistent

[English](AI-ASSISTANT.md) · **Deutsch**

Die Planungsseite erklärt ihr Ergebnis in verständlicher Sprache und beantwortet Fragen
dazu: welcher Arbeitsplatz der Engpass ist, warum ein Auftrag verspätet ist, warum eine
Maschine am Donnerstag stillsteht, was sich unter einer anderen Prioritätsregel ändern
würde. Die Funktion soll zeigen, wie eine KI-Fähigkeit in ein Produkt gebaut gehört — mit
einer funktionierenden Vorbelegung ohne Netz, einer Anbieter-Abstraktion, sauberem
Rückfall, ausdrücklicher Konfiguration und einer benannten Sicherheitslage — und nicht als
dünne Hülle um eine API. Siehe
[ADR 0005](adr/0005-explainable-scheduling-and-optional-ai.md),
[ADR 0014](adr/0014-schedule-chat-on-device-first-with-pluggable-models.md) und
[ADR 0025](adr/0025-hostile-input-on-the-model-path.md).

## Drei Schichten: Analyse, Antworten, dann ein Modell

Der Kerngedanke: Die **Analyse ist deterministisch**, die **Antworten entstehen auf Ihrem
Gerät**, und das **Modell ist optional**.

```
SchedulingResult ──▶ ScheduleExplainer ──▶ ScheduleExplanation        (Engine, rein)
                                              │  strukturiert, sprachneutral
                                              ▼
                                        IScheduleNarrator               (App)
                                        ├─ RuleBasedNarrator        ← Vorbelegung, ohne Netz
                                        └─ AiScheduleNarrator       ← optional, eigener Schlüssel
                                              ▼
                                        ScheduleAssistant               (Fassade: Auswahl + Rückfall)

ScheduleResult + Parameter + Regeln ──▶ ScheduleChatContext            (einer je Lauf)
                                              │
                                              ▼
                                        OfflineScheduleAnswerer         ← Absichten DE/EN, deterministisch
                                              │  + Was-wäre-wenn mit erneutem Planungslauf
                                              ▼
                                        ScheduleChat                    (Fassade: Gespräch + Rückfall)
                                              │
                                              ▼
                                        IChatProvider
                                        ├─ OpenAiCompatibleChatProvider  /chat/completions
                                        ├─ AnthropicChatProvider         /v1/messages
                                        └─ GeminiChatProvider            models/{model}:generateContent
```

1. **`ScheduleExplainer`** (in der reinen Engine) macht aus einem Lauf eine strukturierte
   `ScheduleExplanation`: zusammenfassende Kennzahlen, den Engpass-Arbeitsplatz, die am
   stärksten verspäteten Aufträge mit der Ressource, an der sie gewartet haben, und eine
   **berechnete** Empfehlung: Er plant die anderen Regeln unter begrenztem Budget erneut
   ein und schlägt einen Wechsel nur vor, wenn eine davon das Ergebnis **in der
   Zielfunktion** messbar verbessert. Früher wurden diese Alternativen allein nach
   Verspätung sortiert, was eine Regel empfehlen konnte, die die Verspätung senkt und den
   Strafwert erhöht; jetzt wird nach dem sortiert, was die Suche minimiert. Die Erläuterung
   enthält keinen Fließtext, es kann darin also nichts halluziniert werden.

2. **`OfflineScheduleAnswerer`** beantwortet eine Frage aus `ScheduleChatContext` — dem
   Ergebnis auf der Seite, den Parametern, die es erzeugt haben, den Arbeitszeitregeln des
   Betriebs und den Feiertagen im Horizont. Eine kleine Absichtserkennung versteht deutsche
   und englische Formulierungen zu:

   | Frage nach | Beispiel |
   | --- | --- |
   | dem Engpass | *Welcher Arbeitsplatz ist der Engpass?* |
   | verspäteten Aufträgen und dem Warum | *Welche Aufträge sind verspätet, und warum?* |
   | einem Auftrag | *Wie steht PO-1003?* — seine Arbeitsgänge mit echten Terminen und Unterbrechungen |
   | einem Arbeitsplatz | *Erzähl mir von CNC-300* — belegte Zeit, Auslastung der offenen Zeit, geschlossene Strecken |
   | Stillstand | *Warum stehen die Maschinen zeitweise still?* — geschlossene Zeit nach Grund, Feiertage benannt |
   | den Vorschriften | *Welche Arbeitszeitregeln gelten?* — die geltenden ArbZG-Einstellungen |
   | einem Was-wäre-wenn | *Was wäre mit SPT?* — **plant erneut** mit dieser Regel und vergleicht |
   | einer Zusammenfassung | *Wie sieht der Plan aus?* |

   Die Antworten sind deterministisch und über dieselbe `.resx` lokalisiert wie die Seite.
   Es gibt **drei** Ausgänge, nicht zwei: Eine erkannte Frage bekommt ihre Antwort, eine
   Frage außerhalb des Wortschatzes bekommt den Hilfetext, und eine Frage, die etwas nennt,
   das der Antwortgeber nicht auflösen kann — `PO-9999`, `CNC-30`, ein Arbeitsplatz, dessen
   Bezeichnung auf zwei passt — endet mit *„PO-9999 kann ich nicht finden“*, statt auf die
   nächste passende Absicht durchzufallen. Der alte Präfixvergleich ist der Grund, warum
   `CNC-30` früher mit den Zahlen von `CNC-300` beantwortet wurde.

3. **`IChatProvider`** ist die Nahtstelle für ein Modell. Drei Clients sprechen die
   Protokolle, für die ein Benutzer am ehesten einen Schlüssel besitzt. Jeder ist ein
   schlanker `HttpClient`-Aufruf mit quellgenerierter JSON-Serialisierung und dem Schlüssel
   in der Kopfzeile, die der Anbieter erwartet — nie in der URL und nie im *Pfad*: Der
   Gemini-Modellname ist ein Pfadsegment, wird also gegen `^[A-Za-z0-9._:@-]{1,100}$`
   geprüft und an der Verwendungsstelle maskiert. Jeder ist vierfach begrenzt: ein
   **Budget von 20 Sekunden, das Senden und Lesen des Rumpfs abdeckt**, eine Obergrenze von
   **1 MiB** für die Antwort, eine Ausgabegrenze von **1 024 Token** bei allen dreien
   (früher nur bei Anthropic) und die frühe Abweisung einer zu großen `Content-Length`.

4. **`ScheduleChat`** hält das Gespräch zum aktuellen Lauf, fragt zuerst den Antwortgeber
   auf dem Gerät und schickt, wenn ein Anbieter konfiguriert ist, die kulturinvarianten
   Fakten samt der Antwort vom Gerät als System-Prompt und die letzten Beiträge als
   Gespräch. Eine Frage über **1 000 Zeichen** wird auf dem Gerät beantwortet, und es wird
   **keine Anfrage gestellt**. Das Gespräch ist serialisiert — zwei Fragen können die
   Beitragsliste nicht verschränken —, und ein neuer Lauf bricht die laufende Anfrage ab
   und verwirft ihre Antwort, statt sie unter dem falschen Plan anzuhängen.

   Die Seite schliesst dieses Fenster, statt damit zu leben: Solange ein Lauf läuft,
   sind die Vorschläge, das Eingabefeld und Senden deaktiviert — während eines Laufs
   gibt es keinen Plan, zu dem sich eine Frage ehrlich beantworten liesse. Eine Frage,
   die beim Eintreffen des Laufs schon unterwegs war, wird im Chat als überholt
   gemeldet, als Ergebnis und nicht als Fehler. Sie stillschweigend zu verwerfen ist
   der Grund, warum eine während eines Laufs gestellte Frage früher spurlos verschwand
   — der Planer drückte einen Vorschlag, und nichts geschah.

   Bei **jedem** Anbieterfehler erscheint die Antwort vom Gerät mit einem Hinweis auf den
   Grund, der Chat antwortet also immer. „Jedem“ ist inzwischen wörtlich zu nehmen: Früher
   fing der Code sieben Ausnahmetypen ab, und ein fehlerhafter Rumpf erreichte den Planer
   als rote Fehlerleiste. Jede Antwort ist mit ihrer Quelle gekennzeichnet.

## Was dem Modell gesagt wird — und was ihm nicht anvertraut wird

`ChatFacts.BuildSystemPrompt` ist die einzige Stelle, an der ein Prompt zusammengesetzt
wird. Die Plandaten und die Antwort vom Gerät stehen in benannten Einfassungen
(`<schedule_facts>…</schedule_facts>`, `<on_device_answer>…</on_device_answer>`), die
Anweisung, dass der eingefasste Bereich **Daten und nie eine Anweisung** sind, wird *nach*
den Daten wiederholt, und jeder Wert läuft vorher durch `PromptText`: einzeilige Felder
verlieren Mehrfach-Leerraum sowie `<`, `>`, `#` und Backticks; Blöcke behalten ihre
Zeilenumbrüche und verlieren dieselben Zeichen.

Die Fakten sind zudem **begrenzt**, auf 40 Aufträge, 20 Arbeitsplätze, 12 Arbeitsgänge je
Arbeitsplatz, 10 Verspätungsbefunde und 16 000 Zeichen insgesamt — und jede Kürzung wird im
Prompt *ausgesprochen* („… und 260 weitere Aufträge, hier nicht aufgeführt“), damit eine
gekürzte Liste nicht als der ganze Betrieb gelesen werden kann.

Das ist Minderung, keine Lösung, und die ADR sagt das mit genau diesen Worten. Der Text
innerhalb der Einfassung ist, was ein Mensch in eine Teilebezeichnung oder eine
Auftragsnummer geschrieben hat.

## Mit eigenem Modell (BYOK)

Das Modell ist **standardmäßig aus** — die App ist ohne es vollständig benutzbar. Zum
Einschalten öffnet man auf der Planungsseite das **Zahnrad** auf der Karte
*Planungs-Assistent* und wählt einen Anbieter. Endpunkt und Modell sind mit sinnvollen
Vorbelegungen gefüllt, die sich überschreiben lassen:

| Anbieter | Endpunkt | Vorbelegtes Modell | Schlüssel-Kopfzeile |
| --- | --- | --- | --- |
| OpenAI-kompatibel (OpenAI, OpenRouter, Groq, Mistral, Ollama, LM Studio …) | `https://api.openai.com/v1` | `gpt-4o-mini` | `Authorization: Bearer` |
| Anthropic | `https://api.anthropic.com` | `claude-opus-5` | `x-api-key` + `anthropic-version` + `anthropic-dangerous-direct-browser-access` |
| Google Gemini | `https://generativelanguage.googleapis.com/v1beta` | `gemini-2.5-flash` | `x-goog-api-key` |

Endpunkte müssen absolute HTTPS-URLs sein; HTTP wird nur für Loopback akzeptiert
(`http://localhost:11434/v1` für Ollama). Ein Gemini-Modell muss ein reiner Name sein —
`gemini-2.5-flash`, nicht `models/gemini-2.5-flash` —, was der bewusst in Kauf genommene
Preis des Musters ist, das den Modellnamen aus der Wegewahl der Anfrage heraushält.

> **CORS.** Weil die App eine statische Seite ohne Backend ist, geht die Anfrage **von
> Ihrem Browser** an den Endpunkt, der Anbieter muss also Browser-Anfragen erlauben.
> OpenAI, Anthropic (mit der Direktzugriffs-Kopfzeile, die der Client sendet), Gemini,
> OpenRouter und lokale Modellserver tun das; manche Proxys nicht. Ein blockierter Aufruf
> fällt mit Hinweis auf die Antwort vom Gerät zurück.

## Sicherheit

- Der API-Schlüssel liegt im `localStorage` dieses Browsers, **unter einem Namen je
  Anbieter** und getrennt von den übrigen Assistenteneinstellungen. Dieser Speicher ist
  **kein Tresor** und von jedem Skript derselben Herkunft lesbar. Der Dialog sagt das auf
  dem Bildschirm, nicht nur hier.
- **Der Dialog liest den Schlüssel nie zurück.** Das Feld ist beim Öffnen leer, und ein
  leeres Feld bedeutet „den gespeicherten Schlüssel behalten“, das Geheimnis gelangt also
  nicht zurück ins Dokument. *Diesen Schlüssel vergessen* entfernt ihn mit einem Klick, und
  ein Anbieterwechsel wechselt, welcher gespeicherte Schlüssel gilt, statt den Schlüssel
  des einen Wirts auf einen anderen zu richten.
- Im Assistenten wird überhaupt nichts protokolliert; zwei Quelltext-Scans halten das so.
- Endpunkte müssen absolute HTTPS-URLs sein (HTTP nur für Loopback); URLs mit eingebetteten
  Zugangsdaten, Query oder Fragment werden abgelehnt — und ebenso ein Modellname, der kein
  reiner Bezeichner ist, weil er im Pfad landet.
- Anbieteraufrufe haben ein Budget von 20 Sekunden für Anfrage *und* Rumpf, eine Obergrenze
  von 1 MiB für die Antwort und eine Ausgabegrenze von 1 024 Token. Der Abbruch durch den
  Aufrufer bleibt unterscheidbar; Zeitablauf oder Fehler ergeben einen lokalisierten
  Rückfall ohne rohen Ausnahmetext.
- Weil der Schlüssel im Browser liegt, sollte auf einem gemeinsam genutzten Rechner kein
  Modell aktiviert werden. Ein Produktivbetrieb gehört hinter einen Backend-Proxy mit
  serverseitigem Geheimnisspeicher — siehe [SECURITY.de.md](SECURITY.de.md).
- Gesendet wird ein **begrenzter, bereinigter und eingefasster** Satz von Plandaten:
  Kennzahlen, Arbeitsplatzbezeichnungen, Auftragsnummern, Termine und die geltenden
  Betriebseinstellungen. Es wird nicht behauptet, dass darin keine personenbezogenen Daten
  vorkommen, denn es sind Freitextfelder, die ein Mensch füllt; behauptet wird, dass sie
  neutralisiert, begrenzt und der Anweisung nie übergeordnet sind.

## Tests

Die gesamte Funktion ist ohne Netz testbar:

- `OfflineScheduleAnswerer`: Absichten auf Deutsch und Englisch, Antworten, die die Zahlen
  des Plans tragen, die Bewertungen der Was-wäre-wenn-Vergleiche, Determinismus — und die
  drei unterschiedlichen Ausgänge: beantwortet, nicht verstanden, erkannt-aber-nicht-auflösbar.
- Die drei Anbieter-Clients laufen gegen einen **gestubbten `HttpMessageHandler`**. Die
  Tests prüfen Pfad, Kopfzeilen (einschließlich Anthropics Version und Direktzugriff),
  JSON-Form, Anthropics Zusammenfalten der Beiträge, die Ausgabegrenze bei allen dreien und
  dass eine leere Modellantwort ein Fehler ist und keine leere Sprechblase.
- **Eine Tabelle feindlicher Antworten über alle drei Anbieter**: eine Auswahl `null`, eine
  Nachricht `null`, ein Inhalt `null`, eine leere Kandidatenliste, ein Rumpf, der nie fertig
  ankommt, ein Rumpf über der 1-MiB-Grenze und ein Modellname, der das Ziel der Anfrage
  umschreiben soll. Für jeden wird geprüft, dass er ein typisierter Fehler wird *und* dass
  der Planer trotzdem eine Antwort vom Gerät bekommt.
- `PromptHardeningTests`: die Einfassungen, die wiederholte Datengrenze, das Entfernen der
  Zeichen, die Grenzen je Abschnitt und die Ansage „… und *n* weitere“ für jede Kürzung.
- `ChatConversationSafetyTests`: zwei Fragen können sich nicht verschränken, ein Zurücksetzen
  bricht die laufende Anfrage ab, die Antwort eines überholten Laufs wird verworfen, und eine
  unbeantwortete Frage wird über die Referenz zurückgezogen und nicht über den Index.
- `ChatDuringARunTests`: die Seitenhälfte derselben Geschichte — während eines Laufs ist
  nichts im Gespräch bedienbar, in der Auszeichnung *und* im Behandler, und eine trotzdem
  überholte Frage wird gemeldet statt verworfen.
- `AssistantKeyHandlingTests`: der Speichername je Anbieter, die Übernahme aus einem älteren
  gemeinsamen Wert, „leer heißt behalten“ und dass der Schlüssel in den serialisierten
  Einstellungen fehlt.
- Zwei Quelltext-Scans, die prüfen, dass der Assistent nichts protokolliert.
- bUnit zeichnet das Bedienfeld: Vorschläge, ein angeklickter Vorschlag auf dem Gerät
  beantwortet, eine getippte Frage, das Leeren und der Einstellungsdialog, der Endpunkt und
  Modell mit dem Anbieter wechselt — dazu ein Test, der prüft, dass der gespeicherte
  Schlüssel in keinem gezeichneten `value`-Attribut auftaucht.
- Playwright bedient die echte Seite: eine vorgeschlagene Frage, ein getipptes
  Was-wäre-wenn und dasselbe auf Deutsch.

Zur Gesamtstrategie siehe [TESTING.de.md](TESTING.de.md).
