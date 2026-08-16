01.06.2026 - 17:57
P. Förderer
@Universität Iserlohn, Vertiefung Softwaretechnik
Unter Leitung von Prof. Dr. Doga Arinir

Ziel:
-----

Sate-of-the-Art Webshop (Thema/Context: Holz/Wood)
- Fullstack-Entwicklung (Front-BackEnd)
- HTML/CSS Frontend
- Microservice-Architektur (gRPC)
- Orchestrierung (ASP.NET)
- Paymentfassade mit mind. 2 Anbietern
- FullEvent-Logging
- Back & Abort Handling

Weiteres:
---------
+  Dokumentation
+  Ausarbeitung
+  Vortrag & Demo

--

Bisher / Meanwhile:
-------------------

Theorie nachvollzogen - 18.04.26
IDE Setup & KOmponenten - 22.04.26
Entwurfsmuster - 02.05.2026
Microserviceentwürfe zum Test - 03.05.26
Webfrontendansätze - 01.06.26

Strukturiertes Logging und OpenTelemetry
----------------------------------------

Jeder aktive Service kann über das gemeinsame `IStructuredLogger` ein
strukturiertes Log schreiben. Jeder Eintrag ist ein eigenständiges JSON-Objekt
mit mindestens diesen Feldern:

```json
{
  "timeStamp": "2026-08-16T18:39:01.1638035Z",
  "correlationID": "537c31dc-503b-4cae-9ab0-3b94e227a064",
  "serviceName": "ShopService",
  "logLevel": "INFO",
  "message": "Request completed.",
  "context": {
    "httpMethod": "GET",
    "statusCode": 200
  }
}
```

Unterstützt werden die Log-Level `INFO`, `WARN`, `ERROR` und `DEBUG`. Die
Ausgabe erfolgt fortlaufend über das normale .NET-Logging in die Debug-Konsole
und gleichzeitig als JSONL-Datei. Pro Service und UTC-Kalendertag entsteht eine
Datei nach dem Muster
`Logs/ShopService/ShopService-2026-08-16.jsonl`. Der aktuelle Tag und die
vorherigen 13 Tage werden behalten; ältere Tagesdateien werden beim Start oder
beim nächsten Tageswechsel entfernt.

`Start-VSTOnlineStore.ps1` startet standardmäßig den nativen OpenTelemetry
Collector für Windows. Beim ersten Start wird die gepinnte Version aus den
offiziellen OpenTelemetry-Releases geladen und über die veröffentlichte
SHA-256-Prüfsumme verifiziert. Alle ASP.NET- und gRPC-Komponenten exportieren ihre
strukturierten Einträge zusätzlich per OTLP/gRPC. Die technische OTLP-Datei des
Collectors liegt unter `Logs/OpenTelemetry/vst-online-store.jsonl`.

Jeder eingehende Aufruf erzeugt einen strukturierten Abschluss-Eintrag. Die
über `X-Correlation-ID` propagierte GUID steht im JSON-Feld `correlationID` und
wird zusätzlich als durchsuchbares OTLP-Attribut exportiert; die
Quellkomponente steht in `serviceName` sowie in der OTLP-Ressource
`service.name`.

Ohne Collector kann die Anwendung mit folgendem Schalter gestartet werden; die
fachlichen Abläufe und die täglichen Service-Dateien funktionieren weiterhin;
lediglich der zentrale OTLP-Export ist nicht verfügbar:

```powershell
.\Start-VSTOnlineStore.ps1 -SkipCollector
```

Der `AuditService` bleibt für spätere fachliche Audit-Ereignisse erhalten. Das
technische Sammeln und Persistieren von Logs übernimmt der Collector.
