# AuditService

## Aufgabe

Der AuditService sammelt fachliche Zustandsereignisse aller Laufzeitservices
über RabbitMQ und speichert sie als unveränderliche, chronologisch verknüpfte
Snapshots. Der ShopService kann die Ereigniskette einer Bestellung anschließend
über die Correlation-ID abfragen.

## Schnittstellen, Messaging und Daten

- Adresse: `http://localhost:6686` über HTTP/2
- gRPC-Vertrag: `Contracts/auditservice.proto`
- Operationen: `GetOrderSnapshots` und `GetStatus`
- Aufrufer der gRPC-API: ShopService
- RabbitMQ-Exchange: `vst.audit.events`
- RabbitMQ-Queue: `vst.audit.snapshots`
- Dead-Letter-Queue: `vst.audit.snapshots.dead-letter`
- Persistenz: `Services/AuditService/Data/audit-snapshots.json`

Der Service veröffentlicht keine fachliche REST-API. Die öffentliche Abfrage
läuft über StoreProxy und ShopService. Der Text-Endpunkt `/` ist nur eine
einfache Prozessdiagnose.

## Voraussetzungen

- .NET 10 SDK
- RabbitMQ auf `localhost:5672`
- freier TCP-Port `6686`
- Schreibzugriff auf `Services/AuditService/Data/audit-snapshots.json`
- optional der vom Betriebsskript verwaltete OpenTelemetry Collector

## Start

Bevorzugter Einzelstart vom Repository-Wurzelverzeichnis:

```powershell
.\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName AuditService
```

Direkter Start über das .NET SDK:

```powershell
dotnet restore .\Services\AuditService\AuditService.csproj
dotnet run --project .\Services\AuditService\AuditService.csproj --launch-profile AuditService
```

Andere Services müssen nicht vorher laufen. Der AuditService konsumiert neue
Ereignisse, sobald diese über RabbitMQ eintreffen.

```powershell
.\Start-VSTOnlineStore.ps1 -Action StopService -ServiceName AuditService
```
