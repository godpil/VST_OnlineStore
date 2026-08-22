# StoreBackend

## Aufgabe

Der StoreBackend ist der interne Persistenzadapter des WarehouseService. Er lädt
den Produktbestand aus einer JSON-Datei, liefert Produkte aus und führt
Reservierungen sowie Freigaben atomar aus. Er ist nicht für Browser oder andere
öffentliche Clients bestimmt.

## Schnittstellen und Daten

- Adresse: `http://localhost:6681` über HTTP/2
- gRPC-Vertrag: `Contracts/storebackend.proto`
- Operationen: `GetProducts`, `ReserveProducts` und `ReleaseProducts`
- Persistenz: `StoreBackend/Data/warehouse-products.json`
- Aufrufer: ausschließlich der WarehouseService
- Ausgehende Kommunikation: Audit-Ereignisse über RabbitMQ

Der Text-Endpunkt `/` dient nur als einfache Prozessdiagnose und ersetzt keinen
fachlichen REST-Endpunkt.

## Voraussetzungen

- .NET 10 SDK
- freier TCP-Port `6681`
- RabbitMQ auf `localhost:5672` für Audit-Ereignisse
- optional der vom Betriebsskript verwaltete OpenTelemetry Collector

## Start

Alle Befehle werden im Wurzelverzeichnis des Repositorys ausgeführt.

Bevorzugt über das Betriebsskript:

```powershell
.\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName StoreBackend
```

Das Skript stellt Pakete wieder her, baut das Projekt und verwaltet den Prozess.
Mit bereits aktuellem Build kann `-SkipBuild` ergänzt werden. Direkt über das
.NET SDK lässt sich der Service so starten:

```powershell
dotnet restore .\StoreBackend\StoreBackend.csproj
dotnet run --project .\StoreBackend\StoreBackend.csproj --launch-profile StoreBackend
```

Ein durch das Betriebsskript gestarteter Prozess wird so beendet:

```powershell
.\Start-VSTOnlineStore.ps1 -Action StopService -ServiceName StoreBackend
```
