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

Betriebsskript
--------------

Das Skript `Start-VSTOnlineStore.ps1` stellt das Testskript für dieses Projekt dar,
und sorgt für die Verwaltung des gesamten Stacks oder einzelner Komponenten:

```powershell
# Hilfe zu allen Parametern und Aktionen
.\Start-VSTOnlineStore.ps1 -h

# Gesamten Stack starten, anzeigen und beenden
.\Start-VSTOnlineStore.ps1
.\Start-VSTOnlineStore.ps1 -Action Status
.\Start-VSTOnlineStore.ps1 -Action Stop

# Eine Komponente gezielt starten oder stoppen
.\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName AuditService -SkipBuild
.\Start-VSTOnlineStore.ps1 -Action StopService -ServiceName AuditService

# Alle bekannten Datei- und Logsenken anzeigen
.\Start-VSTOnlineStore.ps1 -Action FileSinks
```

Als `ServiceName` werden `StoreBackend`, `WarehouseService`, `BillingService`,
`InvoiceService`, `AuditService`, `ShopService`, `StoreProxy` und
`OpenTelemetryCollector` unterstützt. Einzelstarts verändern das bestehende
Prozessmanifest, ohne andere Komponenten zu verlieren. Einzelstopps beenden nur
einen anhand von Prozess-ID und Startzeit eindeutig als verwaltet erkannten
Prozess. Abhängigkeiten werden bei der Einzelsteuerung bewusst nicht automatisch
gestartet oder beendet.

`FileSinks` zeigt pro Senke Status, Anzahl, Gesamtgröße, letzte Änderung und
absoluten Pfad. Erfasst werden die täglich rollierenden Service-Logs,
Standardausgabe und Standardfehler, die OTLP-JSONL-Datei, Audit-Snapshots,
Warehouse-Daten und das Prozessmanifest.

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

Der `AuditService` speichert fachliche Snapshots der Bestellzustände getrennt
von den technischen Logs. Jeder Snapshot besitzt eine eigene `eventID`, die
`correlationID` des Bestellvorgangs, `eventType`, `responsibleService`, einen
UTC-Zeitstempel, einen JSON-Payload, `previousEventID`, `actor` und einen der
Statuswerte `SUCCESS`, `FAILURE`, `COMPENSATING` oder `COMPENSATED`.

Die Ereignisse einer Correlation-ID bilden über `previousEventID` eine
unveränderliche Kette. `eventID`, Zeitstempel, Sequenznummer und Verknüpfung
werden atomar im AuditService vergeben. Der aktuelle Datenbankadapter simuliert
eine append-only Tabelle in `Services/AuditService/Data/audit-snapshots.json`.
Die Anwendungsschicht greift ausschließlich über `IAuditSnapshotRepository`
darauf zu, sodass der JSON-Adapter später durch Entity Framework ersetzt werden
kann.

Der AuditService selbst veröffentlicht keinen REST-Endpunkt. Die chronologisch
sortierte Ereigniskette ist ausschließlich über den StoreProxy abrufbar:

```text
GET /audit/orders/{correlationId}
```

Eine unbekannte Correlation-ID liefert `200 OK` mit einem leeren JSON-Array;
eine syntaktisch ungültige GUID liefert `400 Bad Request`.

StoreProxy-Routen
-----------------

Der StoreProxy veröffentlicht ausschließlich die explizit freigegebenen
Routen. Die Shop-Routen werden über YARP weitergeleitet; die Audit-Abfrage
übersetzt der StoreProxy in einen internen gRPC-Aufruf. Methoden,
Gesamt-Timeouts und Limits sind wie folgt festgelegt:

| Route | Methode | Timeout | Rate Limit pro Client |
|---|---|---:|---:|
| `/api/products/featured` | `GET` | 5 Sekunden | 120 pro Minute |
| `/api/payment-providers` | `GET` | 5 Sekunden | 120 pro Minute |
| `/api/checkout` | `POST` | 30 Sekunden | 10 pro Minute |
| `/api/services/status` | `GET` | 5 Sekunden | 30 pro Minute |
| `/audit/orders/{correlationId}` | `GET` | 5 Sekunden | 30 pro Minute |

Der Checkout-Request darf höchstens 65.536 Bytes groß sein. Überschreitungen
werden als JSON mit HTTP 413 beantwortet; Rate-Limit-Verletzungen liefern HTTP
429 und einen `Retry-After`-Header. Automatische Wiederholungen des Checkouts
sind wegen der möglichen Zahlungswirkung bewusst nicht aktiviert.

YARP prüft den ShopService alle zehn Sekunden aktiv über `/health`. Zwei
aufeinanderfolgende Fehlschläge markieren das Ziel als nicht verfügbar.
Zusätzlich erkennt die passive Prüfung Transportfehler und reaktiviert ein Ziel
nach zehn Sekunden. Der Cluster ist mit `PowerOfTwoChoices` bereits auf weitere
ShopService-Instanzen vorbereitet, verwendet aktuell jedoch genau ein Ziel.

Ein eigener YARP-Transform setzt vor jeder Weiterleitung genau die kanonische
`X-Correlation-ID`. Weiterleitungsfehler werden als strukturierte JSON-Antwort
mit Status, neutraler Meldung und Correlation-ID zurückgegeben; interne
Fehlerdetails erscheinen ausschließlich im strukturierten Log.

Zahlungsanbieter
----------------

Der BillingService stellt die drei Adapter `demo`, `paypal` und `stripe`
hinter einer gemeinsamen `IPaymentProvider`-Schnittstelle bereit. Die Fassade
`PaymentProviderResolver` wählt den Adapter anhand des Checkout-Requests aus.
Der Shop lädt die registrierten Adapter dynamisch über
`GET /api/payment-providers`; dadurch muss die Oberfläche bei einem weiteren
registrierten Adapter nicht um eine fest codierte Auswahlliste ergänzt werden.

Der Checkout erwartet neben den Positionen den Provider-Schlüssel:

```json
{
  "items": [
    {
      "productId": "d63f3cb9-e42e-4d3e-a84d-bfe557e049cc",
      "quantity": 1
    }
  ],
  "paymentProvider": "stripe"
}
```

Alle drei Adapter laufen aktuell ausdrücklich im Testbetrieb und bewegen kein
echtes Geld. Eine produktive PayPal- oder Stripe-Anbindung benötigt zusätzlich
Sandbox-/Produktionszugangsdaten, die jeweilige Freigabe im Browser und eine
Webhook-basierte Abschlussverarbeitung. Diese Details bleiben innerhalb des
jeweiligen Adapters und verändern den Shop-Checkout nicht.

Provider-Auswahl, Zahlungsbeginn und Ergebnis werden als strukturierte Logs im
BillingService geschrieben. Fachlich entstehen zusätzlich Audit-Snapshots für
`PAYMENT_PROVIDER_SELECTED`, `PAYMENT_COMPLETED`, Ablehnungen und technische
Provider-Fehler. Zugangsdaten oder Zahlungsinstrumente werden weder in den Logs
noch im Audit-Payload gespeichert.

Scheiss KI