01.06.2026 - 17:57
Philipp Förderer
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
Frühe Webfrontendansätze (inzwischen entfernt) - 01.06.26
(ja und seitdem gibt es Commits =)    )
--

//Ab hier folgt eine Menge AutoGen Doku (inzwischen ist die Qualität aber annehmbar)


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
.\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName PostgreSQL
.\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName AuditService -SkipBuild
.\Start-VSTOnlineStore.ps1 -Action StopService -ServiceName AuditService
.\Start-VSTOnlineStore.ps1 -Action StopService -ServiceName PostgreSQL

# PostgreSQL unabhängig vom restlichen Store verwalten
.\Start-VSTPostgreSQL.ps1 -Action Start
.\Start-VSTPostgreSQL.ps1 -Action Status
.\Start-VSTPostgreSQL.ps1 -Action Stop

# OpenTelemetry Collector unabhängig verwalten
.\Start-VSTOpenTelemetryCollector.ps1 -Action Start
.\Start-VSTOpenTelemetryCollector.ps1 -Action Status
.\Start-VSTOpenTelemetryCollector.ps1 -Action Stop

# Alle bekannten Datei- und Logsenken anzeigen
.\Start-VSTOnlineStore.ps1 -Action FileSinks
```

Als `ServiceName` werden `PostgreSQL`, `StoreBackend`, `WarehouseService`,
`BillingService`, `InvoiceService`, `AuditService`, `ShopService`, `StoreProxy`
und `OpenTelemetryCollector` unterstützt. Einzelstarts verändern das bestehende
Prozessmanifest, ohne andere Komponenten zu verlieren. Einzelstopps beenden nur
einen anhand von Prozess-ID und Startzeit eindeutig als verwaltet erkannten
Prozess. Abhängigkeiten werden bei der Einzelsteuerung bewusst nicht automatisch
gestartet oder beendet.

`FileSinks` zeigt pro Senke Status, Anzahl, Gesamtgröße, letzte Änderung und
absoluten Pfad. Erfasst werden die täglich rollierenden Service-Logs,
Standardausgabe und Standardfehler, die OTLP-JSONL-Datei, den PostgreSQL-Cluster,
das PostgreSQL-Log, Warehouse-Daten und das Prozessmanifest.

Service-Architektur
-------------------

Jeder ausführbare Service besitzt eine eigene Beschreibung mit Voraussetzungen
und Startanleitung:

| Service | Rolle | Dokumentation |
|---|---|---|
| StoreProxy | öffentlicher Einstiegspunkt und YARP-Gateway | [README](StoreProxy/README.md) |
| ShopService | REST-Fassade und fachlicher Orchestrator | [README](Services/ShopService/README.md) |
| WarehouseService | interne Lager- und Katalog-Servicegrenze | [README](Services/WarehouseService/README.md) |
| StoreBackend | interner Persistenzadapter des WarehouseService | [README](StoreBackend/README.md) |
| BillingService | interne Zahlungsfassade | [README](Services/BillingService/README.md) |
| InvoiceService | asynchrone Rechnungs- und PDF-Erstellung | [README](Services/InvoiceService/README.md) |
| AuditService | persistente fachliche Ereigniskette | [README](Services/AuditService/README.md) |

Die aktuellen Systemkontext-, Komponenten-, Sequenz- und Kompensationsdiagramme
sind unter [docs/diagrams](docs/diagrams/README.md) beschrieben. Die editierbare
Quelle ist `docs/diagrams/online-store-architecture.drawio`; die acht SVG-Dateien
sind daraus abgeleitete, inhaltlich gleichwertige Ansichten.

Der öffentliche synchrone Aufrufpfad lautet immer
`Browser -> StoreProxy -> ShopService`. Der StoreProxy ist ausschließlich
YARP-Gateway und kennt keine gRPC-Verträge oder Adressen fachlicher
Downstream-Services. Vor jeder Bestellung prüft der ShopService synchron die
Betriebsbereitschaft von WarehouseService, BillingService, InvoiceService und
AuditService. Den eigentlichen Checkout orchestriert er über WarehouseService
und BillingService; Invoice- und Audit-Ressourcen fragt er über deren interne
gRPC-Schnittstellen ab.

Der BillingService übermittelt erfolgreiche Zahlungen ausschließlich
asynchron über RabbitMQ an den InvoiceService. Fachliche Audit-Ereignisse
werden von den Laufzeitservices ebenfalls ausschließlich über RabbitMQ an den
AuditService übertragen; dessen gRPC-Schnittstelle erlaubt nur Lese- und
Statusabfragen. OTLP-Exporte zum OpenTelemetry Collector sind davon getrennte
Beobachtbarkeitsdaten.

Der StoreBackend ist kein eigenständiger fachlicher Orchestrierungsschritt,
sondern der interne Persistenzadapter des WarehouseService. Deshalb ist die
synchrone gRPC-Verbindung `WarehouseService -> StoreBackend` innerhalb dieser
Servicegrenze zulässig.

Bestellablauf
-------------

Der erfolgreiche Checkout folgt dem aktuellen SAGA-Pfad:

1. Der StoreProxy leitet `POST /api/orders` weiter. Der ShopService übernimmt die
   Correlation-ID als `orderId`, prüft die Betriebsbereitschaft und validiert den
   Warenkorb.
2. `WarehouseService.ReserveCart` reserviert den Bestand; der StoreBackend
   persistiert Produkte und Reservierungs-Ledger.
3. Der ShopService validiert den im Checkout gewählten, laut Konfiguration
   verfügbaren Zahlungsanbieter und ruft `BillingService.ProcessPayment` auf.
   Nur die Payment-Fassade greift auf den konkreten Adapter zu.
4. Nach erfolgreicher Zahlung veröffentlicht der BillingService
   `payment.succeeded`. Erst eine erfolgreiche Veröffentlichung bestätigt, dass
   die Rechnung zur asynchronen Verarbeitung eingeplant ist.
5. Der InvoiceService konsumiert das Ereignis unabhängig vom HTTP-Aufruf,
   erzeugt und persistiert die PDF-Rechnung und schreibt die E-Mail in die
   konfigurierte Senke.
6. Parallel zur Rechnungsverarbeitung bestätigt der ShopService die persistente
   Reservierung über `WarehouseService.CommitCart`. Bei einem Transportfehler
   erfolgt genau ein idempotenter Wiederholungsversuch.
7. Nach erfolgreichem Commit schreibt der ShopService `ORDER_COMPLETED` und der
   öffentliche Endpunkt antwortet mit `201 Created` und der Rechnungs-URL. Auf
   die fertige PDF-Datei wartet der Checkout nicht.

`GetFeaturedProducts` gehört ausschließlich zur Kataloganzeige und wird im
Bestellablauf nicht aufgerufen. Bei Zahlungsablehnung oder Zahlungsfehler wird
die Reservierung über `ReleaseCart` kompensiert. Scheitert nach erfolgreicher
Zahlung die Veröffentlichung des Rechnungsevents, versucht die SAGA zuerst die
Zahlung zu erstatten und anschließend die Reservierung freizugeben. Nicht
verfügbare oder zu langsame Services führen zu strukturierten Logs,
Audit-Snapshots und einer RFC-9457-Fehlerantwort; ein nicht auflösbarer
Kompensations- oder Commitfehler bleibt als Betriebsstörung sichtbar.

PostgreSQL
----------

Der AuditService ist der einzige Service mit relationaler Persistenz. Das
eigenständige Skript `Start-VSTPostgreSQL.ps1` lädt beim ersten Start PostgreSQL
18.6 als gepinntes Windows-Binärarchiv von EDB, prüft dessen
SHA-256-Prüfsumme und initialisiert einen ausschließlich lokal erreichbaren
Cluster auf `127.0.0.1:6688`. Binaries und Daten liegen getrennt unter
`Tools/PostgreSQL/18.6-1` beziehungsweise `Data/PostgreSQL/18` und werden nicht
in Git aufgenommen. `Start-VSTOnlineStore.ps1` bindet diese Verwaltung ein und
koordiniert sie beim Gesamtstart und beim Beenden des Stacks.

Die Datenbank `vst_audit` wird automatisch erstellt. Beim Beenden des Stacks
wird PostgreSQL zuletzt und kontrolliert im Fast-Modus heruntergefahren, sodass
laufende Transaktionen zurückgerollt und die Datendateien konsistent geschlossen
werden. Das lokale Trust-Verfahren ist auf die Entwicklungs- und
Demonstrationsumgebung beschränkt; eine entfernte oder produktive Bereitstellung
benötigt Benutzerrollen, SCRAM-Authentifizierung und ein Secret-Management.

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

Das eigenständige Skript `Start-VSTOpenTelemetryCollector.ps1` verwaltet den
nativen OpenTelemetry Collector für Windows. Beim ersten Start wird die gepinnte
Version aus den offiziellen OpenTelemetry-Releases geladen und über die
veröffentlichte SHA-256-Prüfsumme verifiziert. `Start-VSTOnlineStore.ps1` bindet
diese Verwaltung beim Gesamtstart ein. Alle ASP.NET- und gRPC-Komponenten
exportieren ihre strukturierten Einträge zusätzlich per OTLP/gRPC. Die technische
OTLP-Datei des Collectors liegt unter
`Logs/OpenTelemetry/vst-online-store.jsonl`.

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
werden atomar im AuditService vergeben. Der PostgreSQL-Adapter speichert sie in
der Tabelle `audit_snapshots`; UUID-, Foreign-Key-, Check- und Unique-Constraints
sichern die Struktur, während Datenbanktrigger Änderungen und Löschungen
verhindern. Der JSON-Payload wird als `jsonb` gespeichert. Die Anwendungsschicht
greift weiterhin ausschließlich über `IAuditSnapshotRepository` darauf zu.

Existiert beim ersten Datenbankstart bereits die frühere Datei
`Services/AuditService/Data/audit-snapshots.json`, übernimmt der AuditService
deren validierte Ereigniskette einmalig in eine leere Datenbank. Danach ist
PostgreSQL die aktive Persistenz; die Datei bleibt nur als Legacy-Quelle erhalten.

Der AuditService selbst veröffentlicht keinen REST-Endpunkt. Die chronologisch
sortierte Ereigniskette ist ausschließlich über den StoreProxy abrufbar. Der
StoreProxy leitet den Aufruf per YARP an den ShopService weiter; nur der
ShopService fragt den AuditService intern per gRPC ab:

```text
GET /api/order-audits/{correlationId}/snapshots
```

Eine unbekannte Correlation-ID liefert `200 OK` mit einem leeren JSON-Array;
eine syntaktisch ungültige GUID liefert `400 Bad Request`.

StoreProxy-Routen
-----------------

Der StoreProxy veröffentlicht ausschließlich die explizit freigegebenen
Routen und leitet sie über YARP an den ShopService weiter. Der StoreProxy kennt
keinen fachlichen Downstream-Service; interne gRPC-Aufrufe an Warehouse,
Billing, Invoice und Audit werden ausschließlich durch den ShopService
orchestriert. Methoden, Gesamt-Timeouts und Limits sind wie folgt festgelegt:

| Route | Methode | Timeout | Rate Limit pro Client |
|---|---|---:|---:|
| `/api/products?featured=true` | `GET` | 5 Sekunden | 120 pro Minute |
| `/api/payment-providers` | `GET` | 5 Sekunden | 120 pro Minute |
| `/api/orders` | `POST` | 30 Sekunden | 10 pro Minute |
| `/api/service-statuses` | `GET` | 5 Sekunden | 30 pro Minute |
| `/api/order-audits/{correlationId}/snapshots` | `GET` | 5 Sekunden | 30 pro Minute |
| `/api/invoices/{invoiceId}/pdf` | `GET` | 12 Sekunden | 30 pro Minute |
| `/health` | `GET` | 2 Sekunden | 30 pro Minute |
| `/openapi/v1.json` | `GET` | 5 Sekunden | 120 pro Minute |
| `/swagger/{remainder}` | `GET` | 5 Sekunden | 120 pro Minute |

OpenAPI-Dokumentation
---------------------

Alle öffentlichen HTTP-Endpunkte sind maschinenlesbar nach OpenAPI 3.1
dokumentiert. Das JSON-Dokument ist über den StoreProxy unter
`http://localhost:6680/openapi/v1.json` erreichbar. Die interaktive Swagger UI
steht unter `http://localhost:6680/swagger` bereit und verwendet dasselbe
Dokument. Sie beschreibt Query- und Pfadparameter, JSON-Requests,
Erfolgsantworten, PDF-Ausgaben sowie die RFC-9457-Fehlerantworten.

Die internen gRPC-Schnittstellen sind nicht Teil von OpenAPI. Ihre Verträge
bleiben in den Dateien unter `Contracts/*.proto` dokumentiert.

Der Request zum Erstellen einer Bestellung darf höchstens 65.536 Bytes groß
sein. Erfolgreiche Bestellungen liefern `201 Created`; ihre `orderId` entspricht
der durchgängig propagierten Correlation-ID. Überschreitungen
werden mit HTTP 413 beantwortet; Rate-Limit-Verletzungen liefern HTTP 429 und
einen `Retry-After`-Header. Syntaktisch gültige, aber fachlich nicht
verarbeitbare Bestellungen liefern HTTP 422. Automatische Wiederholungen des
Checkouts sind wegen der möglichen Zahlungswirkung bewusst nicht aktiviert.

Alle öffentlichen HTTP-Fehlerantworten verwenden RFC-9457-Problem-Details mit
dem Medientyp `application/problem+json`. Neben `type`, `title`, `status`,
`detail` und `instance` enthalten sie die Erweiterung `correlationID`;
fachspezifische Antworten können beispielsweise `orderId`, `invoiceId` oder
`retryAfterSeconds` ergänzen.

YARP prüft den ShopService alle zehn Sekunden aktiv über `/health`. Zwei
aufeinanderfolgende Fehlschläge markieren das Ziel als nicht verfügbar.
Zusätzlich erkennt die passive Prüfung Transportfehler und reaktiviert ein Ziel
nach zehn Sekunden. Der Cluster ist mit `PowerOfTwoChoices` bereits auf weitere
ShopService-Instanzen vorbereitet, verwendet aktuell jedoch genau ein Ziel.

Ein eigener YARP-Transform setzt vor jeder Weiterleitung genau die kanonische
`X-Correlation-ID`. Weiterleitungsfehler werden als strukturierte JSON-Antwort
im Problem-Details-Format mit Status, neutraler Meldung und Correlation-ID
zurückgegeben; interne Fehlerdetails erscheinen ausschließlich im
strukturierten Log.

Zahlungsanbieter
----------------

Der BillingService stellt die drei Adapter `demo`, `paypal` und `stripe`
hinter einer gemeinsamen `IPaymentProvider`-Schnittstelle bereit. Die
`PaymentFacade` ist der einzige Zugriffspunkt auf diese Adapter. Über
`PaymentProviders:EnabledProviderKeys` legt die Konfiguration fest, welche
Adapter für neue Bestellungen verwendet werden dürfen. Standardmäßig bleiben
PayPal und Stripe aktiviert; DemoPay ist registriert, aber deaktiviert.

`GET /api/payment-providers` liefert alle registrierten Adapter und kennzeichnet
ihre Verfügbarkeit mit `isEnabled`. Die Website zeigt deaktivierte Adapter
ausgegraut an und trifft beim Laden keine Vorauswahl. Erst der Kunde wählt im
Warenkorb PayPal oder Stripe; der Schlüssel wird für genau diese Bestellung
übertragen und serverseitig erneut gegen die Konfiguration validiert.

Der Checkout enthält deshalb den ausgewählten Provider-Schlüssel:

```json
{
  "items": [
    {
      "productId": "d63f3cb9-e42e-4d3e-a84d-bfe557e049cc",
      "quantity": 1
    }
  ],
  "customerEmail": "kunde@beispiel.de",
  "paymentProviderKey": "paypal"
}
```

Die Fassade bietet einheitlich `ChargeAsync(providerKey, orderId, amount,
currency)`, `RefundAsync` und `GetStatusAsync`. `ActiveProviderKey` bleibt als
serverseitiger Standard für interne oder ältere gRPC-Aufrufer erhalten, führt im
Browser aber zu keiner Vorauswahl. Neue `IPaymentProvider`-Implementierungen
werden automatisch entdeckt; eine Änderung der Fassade oder von `Program.cs`
ist dafür nicht erforderlich.

Alle drei Adapter laufen aktuell ausdrücklich im Testbetrieb und bewegen kein
echtes Geld. Eine produktive PayPal- oder Stripe-Anbindung benötigt zusätzlich
Sandbox-/Produktionszugangsdaten, die jeweilige Freigabe im Browser und eine
Webhook-basierte Abschlussverarbeitung. Diese Details bleiben innerhalb des
jeweiligen Adapters und verändern den Shop-Checkout nicht.

Provider-Auswahl, Zahlungsbeginn, Erstattungen und Ergebnisse werden als strukturierte Logs im
BillingService geschrieben. Fachlich entstehen zusätzlich Audit-Snapshots für
`PAYMENT_PROVIDER_SELECTED`, `PAYMENT_COMPLETED`, Ablehnungen und technische
Provider-Fehler. Zugangsdaten oder Zahlungsinstrumente werden weder in den Logs
noch im Audit-Payload gespeichert.
