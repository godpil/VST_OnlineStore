<#
.SYNOPSIS
    Initialisiert, startet, prueft und verwaltet Das Holzwerk.

.DESCRIPTION
    Start fuehrt einen Restore und Build aus, startet alle benoetigten Prozesse
    in Abhaengigkeitsreihenfolge, prueft den vollstaendigen API-Pfad und oeffnet
    anschliessend die Website im Standardbrowser. RabbitMQ wird ohne Docker als
    extern installierter Windows-Dienst auf Port 5672 vorausgesetzt.

.PARAMETER Action
    Start (Standard), Status, Stop, StartService, StopService oder FileSinks.

.PARAMETER ServiceName
    Komponente für StartService oder StopService. Neben den Anwendungen kann
    auch der OpenTelemetryCollector einzeln verwaltet werden.

.PARAMETER SkipBuild
    Ueberspringt Restore und Build, wenn bereits aktuelle Build-Ausgaben
    vorhanden sind.

.PARAMETER NoBrowser
    Startet und prueft die Anwendung, ohne den Browser zu oeffnen.

.PARAMETER SkipCollector
    Startet die Anwendung ohne den OpenTelemetry Collector.

.PARAMETER Help
    Zeigt mit -h eine kompakte Übersicht aller Skriptparameter und Beispiele.

.EXAMPLE
    .\Start-VSTOnlineStore.ps1

.EXAMPLE
    .\Start-VSTOnlineStore.ps1 -Action Status

.EXAMPLE
    .\Start-VSTOnlineStore.ps1 -Action Stop

.EXAMPLE
    .\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName AuditService -SkipBuild

.EXAMPLE
    .\Start-VSTOnlineStore.ps1 -Action StopService -ServiceName BillingService

.EXAMPLE
    .\Start-VSTOnlineStore.ps1 -Action FileSinks
#>

[CmdletBinding()]
param(
    [ValidateSet("Start", "Status", "Stop", "StartService", "StopService", "FileSinks")]
    [string]$Action = "Start",

    [Alias("Service")]
    [ValidateSet(
        "OpenTelemetryCollector",
        "StoreBackend",
        "WarehouseService",
        "BillingService",
        "InvoiceService",
        "AuditService",
        "ShopService",
        "StoreProxy")]
    [string]$ServiceName,

    [switch]$SkipBuild,

    [switch]$NoBrowser,

    [switch]$SkipCollector,

    [Alias("h")]
    [switch]$Help
)

$ErrorActionPreference = "Stop"

$projectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$solutionPath = Join-Path $projectRoot "VST_OnlineStore.slnx"
$runtimeDirectory = Join-Path $projectRoot "Logs\Startup"
$processManifestPath = Join-Path $runtimeDirectory "holzwerk-processes.json"
$collectorLogDirectory = Join-Path $projectRoot "Logs\OpenTelemetry"
$collectorVersion = "0.157.0"
$collectorArchiveName = "otelcol-contrib_${collectorVersion}_windows_amd64.tar.gz"
$collectorReleaseBaseUrl = "https://github.com/open-telemetry/opentelemetry-collector-releases/releases/download/v$collectorVersion"
$collectorInstallDirectory = Join-Path $projectRoot "Tools\OpenTelemetryCollector\$collectorVersion"
$collectorExecutablePath = Join-Path $collectorInstallDirectory "otelcol-contrib.exe"
$collectorConfigPath = Join-Path $projectRoot "Observability\otel-collector-config.yaml"
$collectorPort = 6687
$rabbitMqPort = 5672
$websiteUrl = "http://localhost:6680/"
$apiReadinessUrl = "http://localhost:6680/api/products/featured"
$paymentProvidersReadinessUrl = "http://localhost:6680/api/payment-providers"
$auditReadinessUrl = "http://localhost:6680/audit/orders/$([Guid]::NewGuid().ToString('D'))"
$browserUrl = "{0}?version=4&started={1}" -f `
    $websiteUrl.TrimEnd("/"), `
    [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

$serviceDefinitions = @(
    [PSCustomObject]@{
        Name = "StoreBackend"
        ProjectDirectory = "StoreBackend"
        Assembly = "StoreBackend.dll"
        Port = 6681
    },
    [PSCustomObject]@{
        Name = "WarehouseService"
        ProjectDirectory = "Services\WarehouseService"
        Assembly = "WarehouseService.dll"
        Port = 6683
    },
    [PSCustomObject]@{
        Name = "BillingService"
        ProjectDirectory = "Services\BillingService"
        Assembly = "BillingService.dll"
        Port = 6684
    },
    [PSCustomObject]@{
        Name = "InvoiceService"
        ProjectDirectory = "Services\InvoiceService"
        Assembly = "InvoiceService.dll"
        Port = 6685
    },
    [PSCustomObject]@{
        Name = "AuditService"
        ProjectDirectory = "Services\AuditService"
        Assembly = "AuditService.dll"
        Port = 6686
    },
    [PSCustomObject]@{
        Name = "ShopService"
        ProjectDirectory = "Services\ShopService"
        Assembly = "ShopService.dll"
        Port = 6682
    },
    [PSCustomObject]@{
        Name = "StoreProxy"
        ProjectDirectory = "StoreProxy"
        Assembly = "StoreProxy.dll"
        Port = 6680
    }
)

function Write-Step {
    param([string]$Message)

    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Show-ScriptHelp {
    Write-Host @"
Das Holzwerk - VST OnlineStore Verwaltung

Syntax:
  .\Start-VSTOnlineStore.ps1 [-Action <Aktion>] [-ServiceName <Komponente>]
                              [-SkipBuild] [-NoBrowser] [-SkipCollector] [-h]

Aktionen:
  Start          Gesamten Stack bauen, starten und pruefen (Standard)
  Status         Status, Ports und verwaltete Prozess-IDs anzeigen
  Stop           Alle durch das Skript verwalteten Prozesse beenden
  StartService   Genau eine Komponente starten
  StopService    Genau eine verwaltete Komponente beenden
  FileSinks      Alle bekannten Datei- und Logsenken anzeigen

Parameter:
  -Action        Auszufuehrende Aktion; Standardwert ist Start
  -ServiceName   Komponente fuer StartService oder StopService
  -Service       Kurzalias fuer -ServiceName
  -SkipBuild     Restore und Build beim Start ueberspringen
  -NoBrowser     Browser beim Gesamtstart nicht oeffnen
  -SkipCollector OpenTelemetry Collector beim Gesamtstart auslassen
  -h             Diese Hilfe anzeigen; es wird keine Aktion ausgefuehrt

Komponenten:
  RabbitMQ (extern), OpenTelemetryCollector, StoreBackend, WarehouseService,
  BillingService, InvoiceService, AuditService, ShopService, StoreProxy

Voraussetzung:
  Ein lokal installierter RabbitMQ-Broker muss ohne Docker auf Port 5672 laufen.
  Die .NET-Anbindung erfolgt mittels des NuGet-Pakets RabbitMQ.Client.

Beispiele:
  .\Start-VSTOnlineStore.ps1
  .\Start-VSTOnlineStore.ps1 -Action Status
  .\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName AuditService -SkipBuild
  .\Start-VSTOnlineStore.ps1 -Action StopService -Service AuditService
  .\Start-VSTOnlineStore.ps1 -Action FileSinks
  .\Start-VSTOnlineStore.ps1 -Action Stop
"@
}

function Test-TcpPort {
    param(
        [string]$HostName = "localhost",
        [Parameter(Mandatory = $true)]
        [int]$Port,
        [int]$TimeoutMilliseconds = 400
    )

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $connection = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $connection.AsyncWaitHandle.WaitOne($TimeoutMilliseconds)) {
            return $false
        }

        $client.EndConnect($connection)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Assert-RabbitMqAvailable {
    if (Test-TcpPort -Port $rabbitMqPort) {
        return
    }

    throw "RabbitMQ ist auf localhost:$rabbitMqPort nicht erreichbar. Bitte den nativ installierten RabbitMQ-Windows-Dienst starten. Docker wird von diesem Projekt nicht verwendet."
}

function Wait-ServicePort {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Service,
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "$($Service.Name) wurde vorzeitig mit Exit-Code $($Process.ExitCode) beendet."
        }

        if (Test-TcpPort -Port $Service.Port) {
            return
        }

        Start-Sleep -Milliseconds 250
    }

    throw "$($Service.Name) ist nicht innerhalb von $TimeoutSeconds Sekunden auf Port $($Service.Port) gestartet."
}

function Get-ManifestEntries {
    if (-not (Test-Path -LiteralPath $processManifestPath)) {
        return @()
    }

    $content = Get-Content -LiteralPath $processManifestPath -Raw
    if ([string]::IsNullOrWhiteSpace($content)) {
        return @()
    }

    $parsedEntries = ConvertFrom-Json $content
    foreach ($entry in $parsedEntries) {
        Write-Output $entry
    }
}

function Save-ManifestEntries {
    param([object[]]$Entries = @())

    $entriesToPersist = @($Entries | Where-Object { $null -ne $_ })
    if ($entriesToPersist.Count -eq 0) {
        Remove-Item `
            -LiteralPath $processManifestPath `
            -Force `
            -ErrorAction SilentlyContinue
        return
    }

    New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
    ConvertTo-Json -InputObject $entriesToPersist |
        Set-Content -LiteralPath $processManifestPath -Encoding UTF8
}

function Get-ComponentDefinition {
    param([Parameter(Mandatory = $true)][string]$Name)

    if ($Name -eq "OpenTelemetryCollector") {
        return [PSCustomObject]@{
            Name = "OpenTelemetryCollector"
            Port = $collectorPort
            IsCollector = $true
        }
    }

    $service = $serviceDefinitions |
        Where-Object { $_.Name -eq $Name } |
        Select-Object -First 1
    if ($null -eq $service) {
        throw "Unbekannte Komponente: $Name"
    }

    return [PSCustomObject]@{
        Name = $service.Name
        Port = $service.Port
        ProjectDirectory = $service.ProjectDirectory
        Assembly = $service.Assembly
        IsCollector = $false
    }
}

function Test-ManifestProcess {
    param([Parameter(Mandatory = $true)][object]$Entry)

    $process = Get-Process -Id $Entry.ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return $false
    }

    try {
        $expectedStart = if ($Entry.StartTimeUtc -is [DateTime]) {
            $Entry.StartTimeUtc.ToUniversalTime()
        }
        else {
            [DateTimeOffset]::Parse(
                [string]$Entry.StartTimeUtc,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind).UtcDateTime
        }
        $actualStart = $process.StartTime.ToUniversalTime()
        return [Math]::Abs(($actualStart - $expectedStart).TotalSeconds) -lt 2
    }
    catch {
        return $false
    }
}

function Show-ApplicationStatus {
    $manifestEntries = @(Get-ManifestEntries)
    $rows = foreach ($service in $serviceDefinitions) {
        $entry = $manifestEntries | Where-Object { $_.Name -eq $service.Name } | Select-Object -First 1
        $processIsRunning = $null -ne $entry -and (Test-ManifestProcess -Entry $entry)

        [PSCustomObject]@{
            Component = $service.Name
            Port = $service.Port
            PortStatus = if (Test-TcpPort -Port $service.Port) { "offen" } else { "geschlossen" }
            ProcessId = if ($processIsRunning) { $entry.ProcessId } else { "-" }
        }
    }

    $collectorEntry = $manifestEntries |
        Where-Object { $_.Name -eq "OpenTelemetryCollector" } |
        Select-Object -First 1
    $collectorIsRunning = $null -ne $collectorEntry -and
        (Test-ManifestProcess -Entry $collectorEntry)
    $collectorRow = [PSCustomObject]@{
        Component = "OpenTelemetryCollector"
        Port = $collectorPort
        PortStatus = if (Test-TcpPort -Port $collectorPort) { "offen" } else { "geschlossen" }
        ProcessId = if ($collectorIsRunning) { $collectorEntry.ProcessId } else { "-" }
    }

    $rabbitMqRow = [PSCustomObject]@{
        Component = "RabbitMQ (external)"
        Port = $rabbitMqPort
        PortStatus = if (Test-TcpPort -Port $rabbitMqPort) { "offen" } else { "geschlossen" }
        ProcessId = "external"
    }

    @($rabbitMqRow, $collectorRow) + @($rows) | Format-Table -AutoSize
}

function Install-OpenTelemetryCollector {
    if (Test-Path -LiteralPath $collectorExecutablePath) {
        return
    }

    Write-Step "OpenTelemetry Collector $collectorVersion installieren"

    if ($null -eq (Get-Command tar -ErrorAction SilentlyContinue)) {
        throw "Das Windows-Werkzeug 'tar' wurde nicht gefunden."
    }

    $temporaryDirectory = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        ("vst-otel-collector-" + [Guid]::NewGuid().ToString("N"))
    $archivePath = Join-Path $temporaryDirectory $collectorArchiveName
    $checksumsPath = Join-Path $temporaryDirectory "windows-checksums.txt"
    $extractedDirectory = Join-Path $temporaryDirectory "extracted"

    New-Item -ItemType Directory -Path $extractedDirectory -Force | Out-Null

    try {
        Invoke-WebRequest `
            -Uri "$collectorReleaseBaseUrl/$collectorArchiveName" `
            -OutFile $archivePath `
            -UseBasicParsing
        Invoke-WebRequest `
            -Uri "$collectorReleaseBaseUrl/opentelemetry-collector-releases_otelcol-contrib_windows_checksums.txt" `
            -OutFile $checksumsPath `
            -UseBasicParsing

        $checksumPattern = "^([a-fA-F0-9]{64})\s+\*?" +
            [Regex]::Escape($collectorArchiveName) + "$"
        $expectedChecksum = $null
        foreach ($line in Get-Content -LiteralPath $checksumsPath) {
            if ($line -match $checksumPattern) {
                $expectedChecksum = $Matches[1].ToLowerInvariant()
                break
            }
        }

        if ([string]::IsNullOrWhiteSpace($expectedChecksum)) {
            throw "Die offizielle Prüfsumme für $collectorArchiveName wurde nicht gefunden."
        }

        $actualChecksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualChecksum -ne $expectedChecksum) {
            throw "Die SHA-256-Prüfsumme des Collector-Archivs ist ungültig."
        }

        & tar -xzf $archivePath -C $extractedDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "Das Collector-Archiv konnte nicht entpackt werden."
        }

        $extractedExecutable = Join-Path $extractedDirectory "otelcol-contrib.exe"
        if (-not (Test-Path -LiteralPath $extractedExecutable)) {
            throw "Das Collector-Archiv enthält keine otelcol-contrib.exe."
        }

        $collectorInstallRoot = Split-Path -Parent $collectorInstallDirectory
        New-Item -ItemType Directory -Path $collectorInstallRoot -Force | Out-Null
        Move-Item -LiteralPath $extractedDirectory -Destination $collectorInstallDirectory
    }
    finally {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "Installiert: $collectorExecutablePath" -ForegroundColor Green
}

function Start-OpenTelemetryCollector {
    Install-OpenTelemetryCollector
    New-Item -ItemType Directory -Path $collectorLogDirectory -Force | Out-Null

    $standardOutputPath = Join-Path $runtimeDirectory "OpenTelemetryCollector.out.log"
    $standardErrorPath = Join-Path $runtimeDirectory "OpenTelemetryCollector.err.log"
    $process = Start-Process `
        -FilePath $collectorExecutablePath `
        -ArgumentList @("--config", ('"{0}"' -f $collectorConfigPath)) `
        -WorkingDirectory $projectRoot `
        -RedirectStandardOutput $standardOutputPath `
        -RedirectStandardError $standardErrorPath `
        -WindowStyle Hidden `
        -PassThru

    $collectorDefinition = [PSCustomObject]@{
        Name = "OpenTelemetryCollector"
        Port = $collectorPort
    }
    Wait-ServicePort -Service $collectorDefinition -Process $process -TimeoutSeconds 30
    Write-Host "Bereit: OpenTelemetry Collector auf Port $collectorPort" -ForegroundColor Green

    return [PSCustomObject]@{
        Name = $collectorDefinition.Name
        ProcessId = $process.Id
        StartTimeUtc = $process.StartTime.ToUniversalTime().ToString("O")
        Port = $collectorDefinition.Port
    }
}

function Stop-Application {
    Write-Step "Das Holzwerk beenden"

    $entries = @(Get-ManifestEntries)
    if ($entries.Count -eq 0) {
        Write-Host "Keine vom Startskript verwalteten Prozesse gefunden."
    }
    else {
        [Array]::Reverse($entries)
        foreach ($entry in $entries) {
            if (-not (Test-ManifestProcess -Entry $entry)) {
                Write-Host "Uebersprungen: $($entry.Name) laeuft nicht mehr."
                continue
            }

            Write-Host "Stoppe $($entry.Name) (PID $($entry.ProcessId)) ..."
            Stop-Process -Id $entry.ProcessId -Force
        }
    }

    Remove-Item -LiteralPath $processManifestPath -Force -ErrorAction SilentlyContinue
    Write-Host "Alle verwalteten Prozesse wurden beendet." -ForegroundColor Green
}

function Invoke-DotNetCommand {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') ist mit Exit-Code $LASTEXITCODE fehlgeschlagen."
    }
}

function Start-ApplicationProcess {
    param([Parameter(Mandatory = $true)][object]$Service)

    $workingDirectory = Join-Path $projectRoot $Service.ProjectDirectory
    $assemblyPath = Join-Path $workingDirectory ("bin\Debug\net10.0\" + $Service.Assembly)
    if (-not (Test-Path -LiteralPath $assemblyPath)) {
        throw "Build-Ausgabe fehlt: $assemblyPath"
    }

    $standardOutputPath = Join-Path $runtimeDirectory ($Service.Name + ".out.log")
    $standardErrorPath = Join-Path $runtimeDirectory ($Service.Name + ".err.log")

    $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source

    # Manche Windows-Umgebungen enthalten gleichzeitig "Path" und "PATH".
    # Windows PowerShell kann dann Start-Process nicht initialisieren. Fuer
    # den Prozessstart werden die Eintraege deshalb kurz zusammengefuehrt
    # und anschliessend exakt wiederhergestellt.
    $pathEntries = @()
    foreach ($key in [Environment]::GetEnvironmentVariables().Keys) {
        if ($key -ieq "Path") {
            $pathEntries += [PSCustomObject]@{
                Key = [string]$key
                Value = [Environment]::GetEnvironmentVariable(
                    $key,
                    [EnvironmentVariableTarget]::Process)
            }
        }
    }

    $normalizedPath = ($pathEntries.Value | ForEach-Object {
        $_ -split ";"
    } | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    } | Select-Object -Unique) -join ";"

    try {
        foreach ($entry in $pathEntries) {
            [Environment]::SetEnvironmentVariable(
                $entry.Key,
                $null,
                [EnvironmentVariableTarget]::Process)
        }
        [Environment]::SetEnvironmentVariable(
            "Path",
            $normalizedPath,
            [EnvironmentVariableTarget]::Process)

        $process = Start-Process `
            -FilePath $dotnetPath `
            -ArgumentList @(
                ('"{0}"' -f $assemblyPath),
                "--urls",
                "http://localhost:$($Service.Port)",
                "--environment",
                "Development"
            ) `
            -WorkingDirectory $workingDirectory `
            -RedirectStandardOutput $standardOutputPath `
            -RedirectStandardError $standardErrorPath `
            -WindowStyle Hidden `
            -PassThru
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            "Path",
            $null,
            [EnvironmentVariableTarget]::Process)
        foreach ($entry in $pathEntries) {
            [Environment]::SetEnvironmentVariable(
                $entry.Key,
                $entry.Value,
                [EnvironmentVariableTarget]::Process)
        }
    }

    Wait-ServicePort -Service $Service -Process $process
    Write-Host "Bereit: $($Service.Name) auf Port $($Service.Port)" -ForegroundColor Green

    return [PSCustomObject]@{
        Name = $Service.Name
        ProcessId = $process.Id
        StartTimeUtc = $process.StartTime.ToUniversalTime().ToString("O")
        Port = $Service.Port
    }
}

function Start-SelectedComponent {
    if ([string]::IsNullOrWhiteSpace($ServiceName)) {
        throw "Für -Action StartService muss -ServiceName angegeben werden."
    }

    $component = Get-ComponentDefinition -Name $ServiceName
    $manifestEntries = @(Get-ManifestEntries)
    $manifestEntry = $manifestEntries |
        Where-Object { $_.Name -eq $component.Name } |
        Select-Object -First 1

    if ($null -ne $manifestEntry -and
        (Test-ManifestProcess -Entry $manifestEntry)) {
        $portStatus = if (Test-TcpPort -Port $component.Port) {
            "offen"
        }
        else {
            "noch nicht offen"
        }
        Write-Host `
            "$($component.Name) wird bereits durch das Skript verwaltet (PID $($manifestEntry.ProcessId), Port $portStatus)." `
            -ForegroundColor Yellow
        return
    }

    if (Test-TcpPort -Port $component.Port) {
        throw "Port $($component.Port) für $($component.Name) ist durch einen nicht verwalteten Prozess belegt."
    }

    $remainingEntries = @($manifestEntries |
        Where-Object { $_.Name -ne $component.Name })
    Save-ManifestEntries -Entries $remainingEntries
    New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null

    Write-Step "$($component.Name) einzeln starten"

    if (-not $component.IsCollector) {
        if ($null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw "Das .NET SDK wurde nicht gefunden. Bitte 'dotnet' installieren oder PATH korrigieren."
        }

        $projectPath = Join-Path `
            (Join-Path $projectRoot $component.ProjectDirectory) `
            ($component.Name + ".csproj")
        if (-not (Test-Path -LiteralPath $projectPath)) {
            throw "Projekt nicht gefunden: $projectPath"
        }

        if (-not $SkipBuild) {
            Write-Step "NuGet-Pakete für $($component.Name) wiederherstellen"
            Invoke-DotNetCommand -Arguments @("restore", $projectPath)

            Write-Step "$($component.Name) bauen"
            Invoke-DotNetCommand -Arguments @("build", $projectPath, "--no-restore")
        }
    }

    $startedEntry = if ($component.IsCollector) {
        Start-OpenTelemetryCollector
    }
    else {
        Start-ApplicationProcess -Service $component
    }

    try {
        Save-ManifestEntries -Entries (@($remainingEntries) + @($startedEntry))
    }
    catch {
        Stop-Process `
            -Id $startedEntry.ProcessId `
            -Force `
            -ErrorAction SilentlyContinue
        throw
    }

    Write-Host `
        "$($component.Name) wurde einzeln gestartet. Abhängige Komponenten werden nicht automatisch gestartet." `
        -ForegroundColor Green
}

function Stop-SelectedComponent {
    if ([string]::IsNullOrWhiteSpace($ServiceName)) {
        throw "Für -Action StopService muss -ServiceName angegeben werden."
    }

    $component = Get-ComponentDefinition -Name $ServiceName
    $manifestEntries = @(Get-ManifestEntries)
    $manifestEntry = $manifestEntries |
        Where-Object { $_.Name -eq $component.Name } |
        Select-Object -First 1
    $remainingEntries = @($manifestEntries |
        Where-Object { $_.Name -ne $component.Name })

    Write-Step "$($component.Name) einzeln stoppen"

    if ($null -eq $manifestEntry) {
        Write-Host "$($component.Name) wird nicht durch das Skript verwaltet."
        if (Test-TcpPort -Port $component.Port) {
            Write-Host `
                "Port $($component.Port) ist trotzdem belegt; der fremde Prozess wird nicht beendet." `
                -ForegroundColor Yellow
        }
        return
    }

    if (-not (Test-ManifestProcess -Entry $manifestEntry)) {
        Save-ManifestEntries -Entries $remainingEntries
        Write-Host `
            "Der verwaltete Prozess von $($component.Name) läuft nicht mehr; der veraltete Manifesteintrag wurde entfernt." `
            -ForegroundColor Yellow
        return
    }

    Write-Host "Stoppe $($component.Name) (PID $($manifestEntry.ProcessId)) ..."
    Stop-Process -Id $manifestEntry.ProcessId -Force
    Wait-Process `
        -Id $manifestEntry.ProcessId `
        -Timeout 10 `
        -ErrorAction SilentlyContinue
    Save-ManifestEntries -Entries $remainingEntries

    Write-Host `
        "$($component.Name) wurde beendet. Abhängige Komponenten bleiben unverändert." `
        -ForegroundColor Green
}

function Format-FileSize {
    param([long]$Bytes)

    if ($Bytes -ge 1GB) {
        return "{0:N2} GB" -f ($Bytes / 1GB)
    }
    if ($Bytes -ge 1MB) {
        return "{0:N2} MB" -f ($Bytes / 1MB)
    }
    if ($Bytes -ge 1KB) {
        return "{0:N2} KB" -f ($Bytes / 1KB)
    }

    return "$Bytes B"
}

function Get-FileSinkSummary {
    param(
        [Parameter(Mandatory = $true)][string]$Category,
        [Parameter(Mandatory = $true)][string]$Owner,
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$FilePattern
    )

    $isDirectorySink = -not [string]::IsNullOrWhiteSpace($FilePattern)
    $pathExists = Test-Path -LiteralPath $Path
    $files = if ($isDirectorySink) {
        if ($pathExists) {
            @(Get-ChildItem `
                -LiteralPath $Path `
                -Filter $FilePattern `
                -File `
                -ErrorAction SilentlyContinue)
        }
        else {
            @()
        }
    }
    elseif ($pathExists) {
        @(Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue)
    }
    else {
        @()
    }

    $totalBytes = if ($files.Count -eq 0) {
        0L
    }
    else {
        [long](($files | Measure-Object -Property Length -Sum).Sum)
    }
    $lastWrite = $files |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty LastWriteTime

    [PSCustomObject]@{
        Category = $Category
        Owner = $Owner
        Status = if ($files.Count -gt 0) {
            "vorhanden"
        }
        elseif ($pathExists) {
            "leer"
        }
        else {
            "fehlt"
        }
        Files = $files.Count
        Size = Format-FileSize -Bytes $totalBytes
        LastWrite = if ($null -eq $lastWrite) { "-" } else { $lastWrite }
        Path = $Path
    }
}

function Show-FileSinks {
    Write-Step "Dateisenken von Das Holzwerk"

    $rows = @()
    foreach ($service in $serviceDefinitions) {
        $rows += Get-FileSinkSummary `
            -Category "Structured JSONL" `
            -Owner $service.Name `
            -Path (Join-Path $projectRoot ("Logs\" + $service.Name)) `
            -FilePattern ($service.Name + "-????-??-??.jsonl")
    }

    $startupComponents = @("OpenTelemetryCollector") + @($serviceDefinitions.Name)
    foreach ($componentName in $startupComponents) {
        $rows += Get-FileSinkSummary `
            -Category "Standard output" `
            -Owner $componentName `
            -Path (Join-Path $runtimeDirectory ($componentName + ".out.log"))
        $rows += Get-FileSinkSummary `
            -Category "Standard error" `
            -Owner $componentName `
            -Path (Join-Path $runtimeDirectory ($componentName + ".err.log"))
    }

    $rows += Get-FileSinkSummary `
        -Category "OTLP JSONL" `
        -Owner "OpenTelemetryCollector" `
        -Path (Join-Path $collectorLogDirectory "vst-online-store.jsonl")
    $rows += Get-FileSinkSummary `
        -Category "Business audit" `
        -Owner "AuditService" `
        -Path (Join-Path $projectRoot "Services\AuditService\Data\audit-snapshots.json")
    $rows += Get-FileSinkSummary `
        -Category "Domain data" `
        -Owner "StoreBackend" `
        -Path (Join-Path $projectRoot "StoreBackend\Data\warehouse-products.json")
    $rows += Get-FileSinkSummary `
        -Category "Process manifest" `
        -Owner "Start script" `
        -Path $processManifestPath

    $rows |
        Sort-Object Category, Owner |
        Format-Table `
            Category, Owner, Status, Files, Size, LastWrite, Path `
            -AutoSize `
            -Wrap
}

function Wait-ApplicationApi {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        try {
            $products = Invoke-RestMethod -Uri $apiReadinessUrl -Method Get -TimeoutSec 5
            $productCount = [int]$products.Count
            if ($productCount -lt 1 -and $null -ne $products) {
                $productCount = 1
            }
            $paymentProviders = @(
                Invoke-RestMethod `
                    -Uri $paymentProvidersReadinessUrl `
                    -Method Get `
                    -TimeoutSec 5)
            $paymentProviderKeys = @($paymentProviders | ForEach-Object { $_.key })
            $hasRequiredPaymentProviders = `
                $paymentProviderKeys -contains "demo" -and `
                $paymentProviderKeys -contains "paypal" -and `
                $paymentProviderKeys -contains "stripe"
            $website = Invoke-WebRequest -Uri $websiteUrl -UseBasicParsing -TimeoutSec 5
            $auditResponse = Invoke-WebRequest `
                -Uri $auditReadinessUrl `
                -UseBasicParsing `
                -TimeoutSec 5
            $auditPayload = ConvertFrom-Json -InputObject $auditResponse.Content
            $auditSnapshotCount = if ($null -eq $auditPayload) {
                0
            }
            else {
                @($auditPayload).Count
            }
            $hasCurrentWebsite = `
                $website.Content.Contains("<title>Das Holzwerk</title>") -and `
                $website.Content.Contains('id="open-cart"') -and `
                $website.Content.Contains('id="payment-provider-cards"') -and `
                $website.Content.Contains('id="checkout-payment-providers"')

            if ($website.StatusCode -eq 200 -and
                $auditResponse.StatusCode -eq 200 -and
                $auditSnapshotCount -eq 0 -and
                $productCount -gt 0 -and
                $hasRequiredPaymentProviders -and
                $hasCurrentWebsite) {
                return $productCount
            }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Der vollstaendige API-Pfad war nicht rechtzeitig erreichbar: $apiReadinessUrl"
}

function Start-Application {
    if (-not (Test-Path -LiteralPath $solutionPath)) {
        throw "Solution nicht gefunden: $solutionPath"
    }

    if ($null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "Das .NET SDK wurde nicht gefunden. Bitte 'dotnet' installieren oder PATH korrigieren."
    }

    Assert-RabbitMqAvailable

    $occupiedServices = @($serviceDefinitions | Where-Object { Test-TcpPort -Port $_.Port })
    if ($occupiedServices.Count -gt 0) {
        $occupiedDescription = ($occupiedServices | ForEach-Object { "$($_.Name):$($_.Port)" }) -join ", "
        throw "Benoetigte Ports sind bereits belegt: $occupiedDescription. Bitte zuerst '.\Start-VSTOnlineStore.ps1 -Action Stop' ausfuehren oder die fremden Prozesse beenden."
    }

    if (-not $SkipCollector -and (Test-TcpPort -Port $collectorPort)) {
        throw "Der Collector-Port $collectorPort ist bereits belegt."
    }

    New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null

    if (-not $SkipBuild) {
        Write-Step "NuGet-Pakete wiederherstellen"
        Invoke-DotNetCommand -Arguments @("restore", $solutionPath)

        Write-Step "Solution bauen"
        Invoke-DotNetCommand -Arguments @("build", $solutionPath, "--no-restore")
    }

    Write-Step "Collector, Backend, Services und Proxy starten"
    $startedProcesses = @()
    try {
        if (-not $SkipCollector) {
            $startedProcesses += Start-OpenTelemetryCollector
        }

        foreach ($service in $serviceDefinitions) {
            $startedProcesses += Start-ApplicationProcess -Service $service
        }

        Save-ManifestEntries -Entries $startedProcesses

        Write-Step "Vollstaendigen Aufrufpfad pruefen"
        $productCount = Wait-ApplicationApi
        Write-Host "$productCount Produkte, Branding, Warenkorb, Payment-Provider und Audit-Abfrage erfolgreich ueber den Proxy geladen." -ForegroundColor Green

        if (-not $NoBrowser) {
            Write-Step "Website oeffnen"
            Start-Process -FilePath $browserUrl
        }

        Write-Host "`nDas Holzwerk ist bereit: $websiteUrl" -ForegroundColor Green
        Write-Host "Status: .\Start-VSTOnlineStore.ps1 -Action Status"
        Write-Host "Dateisenken: .\Start-VSTOnlineStore.ps1 -Action FileSinks"
        Write-Host "Stop:   .\Start-VSTOnlineStore.ps1 -Action Stop"
    }
    catch {
        foreach ($entry in $startedProcesses) {
            $process = Get-Process -Id $entry.ProcessId -ErrorAction SilentlyContinue
            if ($null -ne $process) {
                Stop-Process -Id $entry.ProcessId -Force -ErrorAction SilentlyContinue
            }
        }

        throw
    }
}

if ($Help) {
    Show-ScriptHelp
}
else {
    switch ($Action) {
        "Start" {
            Start-Application
        }
        "Status" {
            Write-Step "Status von Das Holzwerk"
            Show-ApplicationStatus
        }
        "Stop" {
            Stop-Application
        }
        "StartService" {
            Start-SelectedComponent
        }
        "StopService" {
            Stop-SelectedComponent
        }
        "FileSinks" {
            Show-FileSinks
        }
    }
}
