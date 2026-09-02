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
- PostgreSQL: `127.0.0.1:6688`, Datenbank `vst_audit`
- Tabelle: `audit_snapshots`
- Legacy-Import: `Services/AuditService/Data/audit-snapshots.json`

Das relationale Schema verwendet native UUIDs, `timestamptz` und `jsonb`.
Constraints sichern eindeutige Event- und Sequenznummern sowie die Verkettung
über `previous_event_id`. Datenbanktrigger lehnen `UPDATE`, `DELETE` und
`TRUNCATE` ab. Beim ersten Start mit leerer Datenbank werden vorhandene
Snapshots aus der bisherigen JSON-Datei einmalig und in Sequenzreihenfolge
übernommen.

Der Service veröffentlicht keine fachliche REST-API. Die öffentliche Abfrage
läuft über StoreProxy und ShopService. Der Text-Endpunkt `/` ist nur eine
einfache Prozessdiagnose.

Für Betrieb und Vorführung können die gespeicherten Datensätze zusätzlich
direkt und ausschließlich lesend über den projektlokalen PostgreSQL-Client
angezeigt werden. Ohne Filter erscheinen bis zu 100 der neuesten Einträge in
chronologischer Reihenfolge; `-CorrelationId` begrenzt die Ausgabe auf eine
Bestellung und `-Limit` akzeptiert Werte von 1 bis 1000:

```powershell
.\Start-VSTOnlineStore.ps1 -Action DatabaseEntries -Limit 50
.\Start-VSTPostgreSQL.ps1 -Action DatabaseEntries -CorrelationId <Guid>
```

Die Abfrage benötigt nur den laufenden projektlokalen PostgreSQL-Cluster, nicht
den AuditService oder den StoreProxy. Sie verwendet eine feste `SELECT`-Abfrage
und erlaubt keine frei eingegebenen SQL-Anweisungen.

Da fachliche Ereignisse über RabbitMQ eintreffen, kann die öffentliche
Snapshot-Kette unmittelbar nach einer Operation kurzzeitig noch unvollständig
sein. Erfolgreiche Abläufe enden mit `ORDER_COMPLETED/SUCCESS`; fehlgeschlagene
Bestellungen verwenden dieselbe Abschluss-Ereigniskategorie mit dem Zustand
`ORDER_FAILED` und Status `FAILURE`. SAGA-Gegenmaßnahmen werden zusätzlich mit
`COMPENSATING` und `COMPENSATED` sichtbar gemacht, während Folgefehler als
`FAILURE` erhalten bleiben.

## Voraussetzungen

- .NET 10 SDK
- RabbitMQ auf `localhost:5672`
- PostgreSQL auf `127.0.0.1:6688`
- freier TCP-Port `6686`
- optional der vom Betriebsskript verwaltete OpenTelemetry Collector

Das eigenständige Skript `Start-VSTPostgreSQL.ps1` lädt PostgreSQL 18.6 beim
ersten Start als geprüftes Windows-Binärarchiv herunter, initialisiert einen
projektlokalen Datencluster unter `Data/PostgreSQL/18` und erstellt die
Datenbank automatisch. Der Cluster akzeptiert ausschließlich Verbindungen über
die lokale Adresse und ist für die Entwicklungs- und Demonstrationsumgebung
bestimmt.

## Start

Bevorzugter Einzelstart vom Repository-Wurzelverzeichnis:

```powershell
.\Start-VSTPostgreSQL.ps1 -Action Start
.\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName AuditService
```

Direkter Start über das .NET SDK, nachdem PostgreSQL gestartet wurde:

```powershell
dotnet restore .\Services\AuditService\AuditService.csproj
dotnet run --project .\Services\AuditService\AuditService.csproj --launch-profile AuditService
```

Andere fachliche Services müssen nicht vorher laufen. Der AuditService
konsumiert neue Ereignisse, sobald diese über RabbitMQ eintreffen.

```powershell
.\Start-VSTOnlineStore.ps1 -Action StopService -ServiceName AuditService
.\Start-VSTPostgreSQL.ps1 -Action Stop
```
