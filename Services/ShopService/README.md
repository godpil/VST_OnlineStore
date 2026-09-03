# ShopService

## Aufgabe

Der ShopService ist die zentrale REST-Fassade und der fachliche Orchestrator des
OnlineStores. Er koordiniert Produktabfragen, Bestellungen, Bestand,
Zahlungsabwicklung, Rechnungen und Audit-Abfragen. Nur dieser Service kennt die
internen gRPC-Schnittstellen der fachlichen Microservices.

Im regulären Aufrufpfad wird der ShopService ausschließlich über den StoreProxy
angesprochen.

## HTTP-Schnittstelle

- interne Adresse: `http://localhost:6682`
- `GET /api/products?featured=true`
- `GET /api/payment-providers`
- `GET /api/presentation-scenarios`
- `POST /api/orders`
- `GET /api/service-statuses`
- `GET /api/order-audits/{correlationId}/snapshots`
- `GET /api/invoices/{invoiceId}/pdf`
- `GET /health`
- OpenAPI 3.1: `/openapi/v1.json`
- Swagger UI: `/swagger`

Öffentliche Fehlerantworten verwenden RFC-9457-Problem-Details. Der öffentliche
Zugriff erfolgt über dieselben Pfade am StoreProxy auf Port `6680`.

`POST /api/orders` übernimmt die kanonische Correlation-ID als `orderId`.
Erfolgreiche Bestellungen liefern öffentlich `201 Created`; die im Ergebnis
enthaltene Rechnungs-URL kann unmittelbar verwendet werden, obwohl die PDF-Datei
asynchron noch erzeugt werden kann.

## Bestellablauf und Fehlerbehandlung

Vor dem Checkout prüft der ShopService WarehouseService, BillingService,
InvoiceService und AuditService über deren Statusoperationen. Ist mindestens ein
Service nicht verfügbar oder läuft in ein Timeout, beginnt die SAGA nicht und
der Client erhält eine passende RFC-9457-Antwort.

Im Happy Path reserviert der ShopService zuerst den Warenkorb über `ReserveCart`,
prüft anschließend den im Request enthaltenen `paymentProviderKey` gegen die
konfigurationsgesteuert verfügbaren Adapter und startet `ProcessPayment`. Nach
erfolgreicher Zahlung und bestätigter Veröffentlichung von `payment.succeeded`
wird die Reservierung mit `CommitCart` endgültig ausgebucht. Erst danach
entsteht `ORDER_COMPLETED`. Die Rechnung verarbeitet der InvoiceService parallel
über RabbitMQ; `GetFeaturedProducts` ist kein Teil des Bestellablaufs.

Bei einer abgelehnten oder fehlgeschlagenen Zahlung wird `ReleaseCart`
aufgerufen. Konnte das Rechnungsevent nach einer erfolgreichen Zahlung nicht
veröffentlicht werden, versucht die SAGA `RefundPayment` und anschließend
`ReleaseCart`. Für einen vorübergehend nicht erreichbaren Lager-Commit erfolgt
genau ein idempotenter Wiederholungsversuch. Scheitert der Commit danach oder
antwortet er fachlich negativ, werden Zahlung und Reservierung kompensiert.
Jeder Start, Erfolg und Fehler eines Kompensationsschritts wird als eigener
Audit-Snapshot geschrieben. Der Client erhält neben HTTP 409, 422, 502, 503
oder 504 den terminalen Bestellstatus `OUT_OF_STOCK`, `PAYMENT_FAILED`,
`ROLLBACK_COMPLETED` oder `ROLLBACK_FAILED`.

Mit `Start-VSTOnlineStore.ps1 -PresentationMode` liefert
`GET /api/presentation-scenarios` vier deterministische, pro Bestellung
auswählbare Fehlerfälle. Ohne diese Startoption ist die Liste leer und eine
manuell übertragene Szenarioangabe wird abgelehnt.

## Voraussetzungen

- .NET 10 SDK
- WarehouseService auf Port `6683`
- BillingService auf Port `6684`
- InvoiceService auf Port `6685`
- AuditService auf Port `6686`
- indirekt PostgreSQL auf Port `6688` für den AuditService
- indirekt StoreBackend auf Port `6681` für den WarehouseService
- RabbitMQ auf `localhost:5672`
- freier TCP-Port `6682`
- optional der vom OpenTelemetry-Subscript verwaltete Collector

## Start

Für den vollständigen Aufrufpfad ist der Start des gesamten Stacks über das
Start-Skript die einfachste Variante:

```powershell
.\Start-VSTOnlineStore.ps1 -NoBrowser
.\Start-VSTOnlineStore.ps1 -PresentationMode -NoBrowser
```

Ein Einzelstart startet die genannten Abhängigkeiten nicht automatisch:

```powershell
.\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName ShopService
```

Direkt über das .NET SDK kann der Service gestartet werden, wenn alle internen
Abhängigkeiten bereits erreichbar sind:

```powershell
dotnet restore .\Services\ShopService\ShopService.csproj
dotnet run --project .\Services\ShopService\ShopService.csproj --launch-profile ShopService
```

Ein verwalteter Einzelprozess wird so beendet:

```powershell
.\Start-VSTOnlineStore.ps1 -Action StopService -ServiceName ShopService
```

## Integrationstests

```powershell
dotnet test .\Tests\ShopService.IntegrationTests\ShopService.IntegrationTests.csproj
```

Die Tests durchlaufen die öffentliche Bestellressource für den Happy Path,
unzureichenden Bestand (`fail1`) und eine Zahlungsablehnung mit Freigabe der
Bestandsreservierung (`fail2`).
