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
- `POST /api/orders`
- `GET /api/service-statuses`
- `GET /api/order-audits/{correlationId}/snapshots`
- `GET /api/invoices/{invoiceId}/pdf`
- `GET /health`
- OpenAPI 3.1: `/openapi/v1.json`
- Swagger UI: `/swagger`

Öffentliche Fehlerantworten verwenden RFC-9457-Problem-Details. Der öffentliche
Zugriff erfolgt über dieselben Pfade am StoreProxy auf Port `6680`.

## Voraussetzungen

- .NET 10 SDK
- WarehouseService auf Port `6683`
- BillingService auf Port `6684`
- InvoiceService auf Port `6685`
- AuditService auf Port `6686`
- indirekt StoreBackend auf Port `6681` für den WarehouseService
- RabbitMQ auf `localhost:5672`
- freier TCP-Port `6682`
- optional der vom Betriebsskript verwaltete OpenTelemetry Collector

## Start

Für den vollständigen Aufrufpfad ist der Gesamtstart die einfachste Variante:

```powershell
.\Start-VSTOnlineStore.ps1 -NoBrowser
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
