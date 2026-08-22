# WarehouseService

## Aufgabe

Der WarehouseService bildet die interne fachliche Servicegrenze für Katalog und
Lagerbestand. Er stellt dem ShopService Produkte bereit, reserviert komplette
Warenkörbe und gibt Reservierungen bei einer Kompensation wieder frei. Die
Persistenz bleibt hinter dem StoreBackend verborgen.

## Schnittstellen und Abhängigkeiten

- Adresse: `http://localhost:6683` über HTTP/2
- gRPC-Vertrag: `Contracts/warehouseservice.proto`
- Operationen: `GetFeaturedProducts`, `ReserveCart`, `ReleaseCart` und `GetStatus`
- Aufrufer: ShopService
- Abhängigkeit: StoreBackend unter `http://localhost:6681`
- Ausgehende Kommunikation: Audit-Ereignisse über RabbitMQ

Der Service veröffentlicht keine fachliche REST-API. Der Text-Endpunkt `/` ist
nur eine einfache Prozessdiagnose.

## Voraussetzungen

- .NET 10 SDK
- laufender StoreBackend auf Port `6681`
- RabbitMQ auf `localhost:5672`
- freier TCP-Port `6683`
- optional der vom Betriebsskript verwaltete OpenTelemetry Collector

## Start

Das Betriebsskript startet Abhängigkeiten bei einem Einzelstart nicht
automatisch. Vom Repository-Wurzelverzeichnis aus ist daher folgende Reihenfolge
sinnvoll:

```powershell
.\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName StoreBackend
.\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName WarehouseService
```

Alternativ kann der WarehouseService direkt gestartet werden, sofern der
StoreBackend bereits erreichbar ist:

```powershell
dotnet restore .\Services\WarehouseService\WarehouseService.csproj
dotnet run --project .\Services\WarehouseService\WarehouseService.csproj --launch-profile WarehouseService
```

Ein verwalteter Einzelprozess wird mit folgendem Befehl beendet:

```powershell
.\Start-VSTOnlineStore.ps1 -Action StopService -ServiceName WarehouseService
```
