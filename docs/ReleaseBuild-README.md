# VST OnlineStore 1.0 – manueller Betrieb

Diese Anleitung beschreibt den manuellen Start des Release-Builds ohne das
Start-Skript `Start-VSTOnlineStore.ps1` und ohne dessen Subscripts. Alle Befehle sind für
PowerShell unter Windows angegeben. Für jeden dauerhaft laufenden Prozess wird
ein eigenes Terminal benötigt.

## 1. Verzeichnis und Voraussetzungen

Die Anleitung geht von diesem Projektverzeichnis aus:

```powershell
$ProjectRoot = "C:\MASTER\VST_OnlineStore"
$ReleaseRoot = Join-Path $ProjectRoot "ReleaseBuild"
```

Falls das Projekt an einer anderen Stelle liegt, muss nur `$ProjectRoot`
angepasst werden.

Erforderlich sind:

- Windows x64
- .NET 10 SDK oder ASP.NET Core Runtime 10
- ein auf `localhost:5672` erreichbarer RabbitMQ-Broker, nativ oder in Docker
- PostgreSQL 18 mit den Programmen `pg_ctl`, `initdb`, `createdb` und `psql`
- OpenTelemetry Collector Contrib 0.157.0 für die zentrale Logsammlung
- freie TCP-Ports 5672 und 6680 bis 6688

Die aktuelle Projektinstallation erwartet PostgreSQL unter
`Tools\PostgreSQL\18.6-1`, den Collector unter
`Tools\OpenTelemetryCollector\0.157.0` und den PostgreSQL-Datencluster unter
`Data\PostgreSQL\18`. Alternativ können eigene Installationen verwendet werden;
dann sind die Pfade in den folgenden Befehlen anzupassen.

| Komponente | Port | Protokoll/Funktion |
|---|---:|---|
| RabbitMQ | 5672 | AMQP |
| StoreProxy | 6680 | öffentliche Website und REST-API |
| StoreBackend | 6681 | internes gRPC-Backend |
| ShopService | 6682 | REST-Orchestrierung und interne API |
| WarehouseService | 6683 | internes gRPC-Lager |
| BillingService | 6684 | interne gRPC-Zahlungsfassade |
| InvoiceService | 6685 | interne gRPC-Rechnungsabfrage |
| AuditService | 6686 | interne gRPC-Auditabfrage |
| OpenTelemetry Collector | 6687 | OTLP/gRPC |
| PostgreSQL | 6688 | Audit-Datenbank `vst_audit` |

## 2. Infrastruktur starten

### 2.1 RabbitMQ

Das Projekt setzt nur einen erreichbaren AMQP-Endpunkt voraus. RabbitMQ kann
nativ als Windows-Dienst oder in einem Docker-Container betrieben werden.

Für die native Variante wird eine PowerShell mit den nötigen Rechten geöffnet:

```powershell
Get-Service -Name RabbitMQ
Start-Service -Name RabbitMQ
Test-NetConnection -ComputerName localhost -Port 5672
```

Eine mögliche Docker-Variante veröffentlicht AMQP auf demselben Host-Port. Der
Management-Port 15672 ist für den OnlineStore nicht erforderlich, ermöglicht
aber den Zugriff auf die RabbitMQ-Weboberfläche:

```powershell
docker run --detach `
    --name vst-rabbitmq `
    --publish 5672:5672 `
    --publish 15672:15672 `
    --env RABBITMQ_DEFAULT_USER=vst `
    --env RABBITMQ_DEFAULT_PASS=<Passwort> `
    --volume vst-rabbitmq-data:/var/lib/rabbitmq `
    rabbitmq:management

Test-NetConnection -ComputerName localhost -Port 5672
```

Die Standardkonfiguration verwendet den virtuellen Host `/` sowie lokal die
Zugangsdaten `guest` / `guest`. Bei einer abweichenden Installation müssen die
`RabbitMq`-Werte in den `appsettings.json`-Dateien der Release-Komponenten oder
mittels Umgebungsvariablen angepasst werden. Für Docker wird ein eigener
Benutzer empfohlen, weil RabbitMQ den Standardbenutzer `guest` normalerweise
auf lokale Verbindungen innerhalb des Brokers beschränkt.

### 2.2 PostgreSQL

Die folgenden Variablen werden in einem neuen Terminal gesetzt:

```powershell
$ProjectRoot = "C:\MASTER\VST_OnlineStore"
$PgBin = Join-Path $ProjectRoot "Tools\PostgreSQL\18.6-1\bin"
$PgData = Join-Path $ProjectRoot "Data\PostgreSQL\18"
$PgLogDirectory = Join-Path $ProjectRoot "Logs\PostgreSQL"
$PgLog = Join-Path $PgLogDirectory "postgresql.log"
New-Item -ItemType Directory -Path $PgLogDirectory -Force | Out-Null
```

Nur wenn noch kein Cluster vorhanden ist, wird er einmalig initialisiert:

```powershell
if (-not (Test-Path (Join-Path $PgData "PG_VERSION"))) {
    New-Item -ItemType Directory -Path $PgData -Force | Out-Null
    & "$PgBin\initdb.exe" `
        "--pgdata=$PgData" `
        "--username=postgres" `
        "--encoding=UTF8" `
        "--locale=C" `
        "--auth-host=trust" `
        "--auth-local=trust"
}
```

Anschließend wird der Server gestartet:

```powershell
& "$PgBin\pg_ctl.exe" start `
    -D $PgData `
    -l $PgLog `
    -o "-h localhost -p 6688" `
    -w `
    -t 60
```

Die Datenbank wird nur beim ersten Mal angelegt. Das Schema und spätere
Schemaänderungen übernimmt der AuditService beim Start über EF-Core-Migrationen.

```powershell
$databaseExists = & "$PgBin\psql.exe" `
    --host=127.0.0.1 `
    --port=6688 `
    --username=postgres `
    --dbname=postgres `
    --tuples-only `
    --no-align `
    --command="SELECT 1 FROM pg_database WHERE datname = 'vst_audit';"

if (($databaseExists -join "").Trim() -ne "1") {
    & "$PgBin\createdb.exe" `
        --host=127.0.0.1 `
        --port=6688 `
        --username=postgres `
        --encoding=UTF8 `
        vst_audit
}
```

### 2.3 OpenTelemetry Collector

Der Collector muss aus dem Projektstamm gestartet werden, damit die relative
Logausgabe in `Logs\OpenTelemetry` landet:

```powershell
$ProjectRoot = "C:\MASTER\VST_OnlineStore"
$Collector = Join-Path $ProjectRoot "Tools\OpenTelemetryCollector\0.157.0\otelcol-contrib.exe"
$CollectorConfig = Join-Path $ProjectRoot "Observability\otel-collector-config.yaml"
Set-Location $ProjectRoot
& $Collector --config $CollectorConfig
```

Das Terminal bleibt geöffnet. Der Collector nimmt OTLP/gRPC auf Port 6687 an.

## 3. Services manuell starten

In **jedem neuen Service-Terminal** werden zunächst dieselben Basisvariablen
gesetzt:

```powershell
$ProjectRoot = "C:\MASTER\VST_OnlineStore"
$ReleaseRoot = Join-Path $ProjectRoot "ReleaseBuild"
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:6687"
$env:VST_STRUCTURED_LOG_DIRECTORY = Join-Path $ProjectRoot "Logs"
```

Wurde der oben gezeigte Docker-Benutzer verwendet, müssen in jedem
Service-Terminal zusätzlich dessen Zugangsdaten gesetzt werden:

```powershell
$env:RabbitMq__HostName = "localhost"
$env:RabbitMq__Port = "5672"
$env:RabbitMq__UserName = "vst"
$env:RabbitMq__Password = "<Passwort>"
```

Für den optionalen Vorführmodus muss zusätzlich vor dem Start jedes Services
folgende Variable gesetzt werden:

```powershell
$env:PresentationMode__Enabled = "true"
```

Ohne diese Variable läuft der normale Shopbetrieb. Die empfohlene manuelle
Startreihenfolge lautet wie folgt.

### 3.1 AuditService

Der frühe Start stellt sicher, dass die Audit-Queue vor den Ereignisproduzenten
existiert. PostgreSQL und RabbitMQ müssen bereits laufen.

```powershell
Set-Location (Join-Path $ReleaseRoot "AuditService")
dotnet .\AuditService.dll --urls http://localhost:6686 --environment Production
```

### 3.2 InvoiceService

Der frühe Start legt die Queue für erfolgreiche Zahlungsereignisse an.

```powershell
Set-Location (Join-Path $ReleaseRoot "InvoiceService")
dotnet .\InvoiceService.dll --urls http://localhost:6685 --environment Production
```

Rechnungen werden unter `ReleaseBuild\InvoiceService\Data` gespeichert. Der
Pickup-E-Mail-Modus legt erzeugte Nachrichten unter `Data\email-outbox` ab.

### 3.3 StoreBackend

```powershell
Set-Location (Join-Path $ReleaseRoot "StoreBackend")
dotnet .\StoreBackend.dll --urls http://localhost:6681 --environment Production
```

Bestand und Reservierungen werden in `ReleaseBuild\StoreBackend\Data`
gespeichert.

### 3.4 WarehouseService

```powershell
Set-Location (Join-Path $ReleaseRoot "WarehouseService")
dotnet .\WarehouseService.dll --urls http://localhost:6683 --environment Production
```

### 3.5 BillingService

```powershell
Set-Location (Join-Path $ReleaseRoot "BillingService")
dotnet .\BillingService.dll --urls http://localhost:6684 --environment Production
```

Die freigegebenen Zahlungsanbieter und Timeouts stehen in
`ReleaseBuild\BillingService\appsettings.json`. Konfigurationswerte können nach
dem üblichen ASP.NET-Core-Schema auch per Umgebungsvariable überschrieben
werden, beispielsweise `PaymentProviders__TimeoutMilliseconds`.

### 3.6 ShopService

```powershell
Set-Location (Join-Path $ReleaseRoot "ShopService")
dotnet .\ShopService.dll --urls http://localhost:6682 --environment Production
```

### 3.7 StoreProxy

Der StoreProxy wird zuletzt gestartet, damit die öffentliche Oberfläche erst
nach den internen Komponenten erreichbar wird.

```powershell
Set-Location (Join-Path $ReleaseRoot "StoreProxy")
dotnet .\StoreProxy.dll --urls http://localhost:6680 --environment Production
```

Die Website-Dateien liegen vollständig im Unterordner `wwwroot` der
Release-Ausgabe und benötigen keinen Zugriff auf das Quellprojekt.

## 4. Betrieb prüfen und Shop verwenden

In einem weiteren PowerShell-Fenster können die Ports geprüft werden:

```powershell
6680..6688 | ForEach-Object {
    [pscustomobject]@{
        Port = $_
        Reachable = Test-NetConnection localhost -Port $_ -InformationLevel Quiet
    }
} | Format-Table -AutoSize
```

Port 6687 ist optional, falls bewusst ohne OpenTelemetry gearbeitet wird. Die
öffentliche Betriebsbereitschaft wird über den Proxy geprüft:

```powershell
Invoke-RestMethod http://localhost:6680/api/service-statuses
Start-Process http://localhost:6680/
```

Weitere Einstiegspunkte:

- Website: `http://localhost:6680/`
- Swagger UI: `http://localhost:6680/swagger`
- OpenAPI 3.1: `http://localhost:6680/openapi/v1.json`
- Produktabfrage: `http://localhost:6680/api/products?featured=true`

Die zentrale strukturierte Logausgabe liegt unter `Logs`, die vom Collector
geschriebene OTLP-Datei unter `Logs\OpenTelemetry\vst-online-store.jsonl`.

## 5. Komponenten kontrolliert beenden

Die sieben .NET-Terminals werden in umgekehrter Reihenfolge mit `Strg+C`
beendet: StoreProxy, ShopService, BillingService, WarehouseService,
StoreBackend, InvoiceService und AuditService. Danach wird der Collector mit
`Strg+C` beendet.

PostgreSQL wird in dem Terminal mit den bereits gesetzten `$PgBin`- und
`$PgData`-Variablen kontrolliert gestoppt:

```powershell
& "$PgBin\pg_ctl.exe" stop -D $PgData -m fast -w -t 30
```

RabbitMQ kann anschließend über eine PowerShell mit den nötigen Rechten beendet
werden:

```powershell
Stop-Service -Name RabbitMQ
```

Das Beenden von RabbitMQ ist nicht erforderlich, wenn der Broker auch von
anderen lokalen Anwendungen verwendet wird.

## 6. Hinweise zu den Daten

- Ein erneuter Release-Build kann als Inhalt markierte JSON-Ausgangsdateien in
  den Release-Ordner kopieren. Vor einem Neuaufbau sollten dort entstandene
  Demonstrationsdaten bei Bedarf gesichert werden.
- PostgreSQL-Daten liegen außerhalb des Release-Ordners unter
  `Data\PostgreSQL\18` und bleiben bei einem Build erhalten.
- Der Ordner `ReleaseBuild` ist ein generiertes Build-Artefakt und wird von Git
  ignoriert. Diese README wird bei jedem Release-Build aus
  `docs\ReleaseBuild-README.md` erneut kopiert.
