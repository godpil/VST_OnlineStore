# StoreProxy

## Aufgabe

Der StoreProxy ist der einzige öffentliche Einstiegspunkt des OnlineStores. Er
liefert das statische Frontend aus und leitet explizit freigegebene HTTP-Routen
über YARP an den ShopService weiter. Zusätzlich erzwingt er Timeouts,
Requestgrößen, Rate Limits, Correlation-IDs und einheitliche
RFC-9457-Fehlerantworten.

Der StoreProxy kennt keine gRPC-Verträge und kommuniziert mit keinem fachlichen
Service außer dem ShopService.

## Öffentliche Schnittstelle

- Adresse: `http://localhost:6680`
- Website: `/`
- REST-API: `/api/*`
- Health-Endpunkt: `/health`
- OpenAPI 3.1: `/openapi/v1.json`
- Swagger UI: `/swagger`
- Weiterleitungsziel: ShopService unter `http://localhost:6682`

Das OpenAPI-Dokument verwendet bewusst die relative Serveradresse `/`.
Dadurch sendet die Swagger-UI auch ihre interaktiven `Try it out`-Aufrufe
immer über den öffentlichen StoreProxy und nicht direkt an den internen
ShopService-Port.

Die Website liest alle Zahlungsanbieter samt `isEnabled` über
`GET /api/payment-providers`. Deaktivierte Adapter werden ausgegraut; für PayPal
und Stripe entstehen auswählbare Optionen. Beim Öffnen des Warenkorbs ist kein
Anbieter vorausgewählt. Erst nach einer bewussten Auswahl wird
`paymentProviderKey` mit `POST /api/orders` übertragen. Ein erfolgreicher
Checkout wird unverändert als `201 Created` samt Rechnungs-URL an den Browser
zurückgegeben.

Die freigegebenen Routen, Timeouts, Rate-Limit-Policies und Health Checks stehen
in `StoreProxy/appsettings.json`.

## Voraussetzungen

- .NET 10 SDK
- laufender ShopService auf Port `6682`
- für einen vollständigen ShopService-Aufruf dessen interne Abhängigkeiten
- RabbitMQ auf `localhost:5672` für Audit-Ereignisse
- freier TCP-Port `6680`
- optional der vom Betriebsskript verwaltete OpenTelemetry Collector

## Start

Für Website und API wird der Gesamtstart empfohlen:

```powershell
.\Start-VSTOnlineStore.ps1
```

Ohne Browser oder nur als einzelner Prozess:

```powershell
.\Start-VSTOnlineStore.ps1 -NoBrowser
.\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName StoreProxy
```

Der Einzelstart startet den ShopService und dessen Abhängigkeiten nicht
automatisch. Ein direkter Start ist ebenfalls möglich:

```powershell
dotnet restore .\StoreProxy\StoreProxy.csproj
dotnet run --project .\StoreProxy\StoreProxy.csproj --launch-profile http
```

Ein verwalteter Einzelprozess wird so beendet:

```powershell
.\Start-VSTOnlineStore.ps1 -Action StopService -ServiceName StoreProxy
```
