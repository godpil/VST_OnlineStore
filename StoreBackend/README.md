# StoreBackend

## Aufgabe

Der StoreBackend ist der interne Persistenzadapter des WarehouseService. Er lädt
den Produktbestand aus einer JSON-Datei, liefert Produkte aus und führt
Reservierungen, finale Ausbuchungen sowie Freigaben aus. Der persistente
Reservierungsstatus macht wiederholte Reserve-, Commit- und Release-Aufrufe
idempotent. Er ist nicht für Browser oder andere öffentliche Clients bestimmt.

## Schnittstellen und Daten

- Adresse: `http://localhost:6681` über HTTP/2
- gRPC-Vertrag: `Contracts/storebackend.proto`
- Operationen: `GetProducts`, `ReserveProducts`, `CommitProducts` und `ReleaseProducts`
- Produktbestand: `StoreBackend/Data/warehouse-products.json`
- Reservierungs-Ledger: `StoreBackend/Data/warehouse-products.reservations.json`
- Aufrufer: ausschließlich der WarehouseService
- Ausgehende Kommunikation: Audit-Ereignisse über RabbitMQ

Der Text-Endpunkt `/` dient nur als einfache Prozessdiagnose und ersetzt keinen
fachlichen REST-Endpunkt.

## Voraussetzungen

- .NET 10 SDK
- freier TCP-Port `6681`
- RabbitMQ auf `localhost:5672` für Audit-Ereignisse
- optional der vom OpenTelemetry-Subscript verwaltete Collector

## Start

Alle Befehle werden im Wurzelverzeichnis des Repositorys ausgeführt.

Bevorzugt über das Start-Skript:

```powershell
.\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName StoreBackend
```

Das Start-Skript stellt Pakete wieder her, baut das Projekt und verwaltet den Prozess.
Mit bereits aktuellem Build kann `-SkipBuild` ergänzt werden. Direkt über das
.NET SDK lässt sich der Service so starten:

```powershell
dotnet restore .\StoreBackend\StoreBackend.csproj
dotnet run --project .\StoreBackend\StoreBackend.csproj --launch-profile StoreBackend
```

Ein durch das Start-Skript gestarteter Prozess wird so beendet:

```powershell
.\Start-VSTOnlineStore.ps1 -Action StopService -ServiceName StoreBackend
```
