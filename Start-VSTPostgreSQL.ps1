<#
.SYNOPSIS
    Installiert und verwaltet den projektlokalen PostgreSQL-Server.

.DESCRIPTION
    Laedt PostgreSQL beim ersten Start als geprueftes Windows-Binaerarchiv,
    initialisiert den lokalen Datencluster und verwaltet die Datenbank
    vst_audit auf Port 6688. Das Skript kann eigenstaendig ausgefuehrt oder
    vom Startskript des OnlineStores eingebunden werden.

.PARAMETER PostgreSqlAction
    Start (Standard), Status, Stop oder DatabaseEntries. Kann über den kurzen
    Parameternamen -Action angegeben werden.

.PARAMETER PostgreSqlCorrelationId
    Optionale Correlation-ID für die Aktion DatabaseEntries. Kann über den
    kurzen Parameternamen -CorrelationId angegeben werden.

.PARAMETER PostgreSqlLimit
    Maximale Anzahl der neuesten Datensätze für DatabaseEntries. Kann über den
    kurzen Parameternamen -Limit angegeben werden; Standardwert ist 100.

.PARAMETER PostgreSqlHelp
    Zeigt über -h oder -Help eine kompakte Uebersicht und Beispiele.

.EXAMPLE
    .\Start-VSTPostgreSQL.ps1

.EXAMPLE
    .\Start-VSTPostgreSQL.ps1 -Action Status

.EXAMPLE
    .\Start-VSTPostgreSQL.ps1 -Action Stop

.EXAMPLE
    .\Start-VSTPostgreSQL.ps1 -Action DatabaseEntries -Limit 50

.EXAMPLE
    .\Start-VSTPostgreSQL.ps1 -Action DatabaseEntries -CorrelationId <Guid>
#>

[CmdletBinding()]
param(
    [Alias("Action")]
    [ValidateSet("Start", "Status", "Stop", "DatabaseEntries")]
    [string]$PostgreSqlAction = "Start",

    [Alias("CorrelationId")]
    [Guid]$PostgreSqlCorrelationId = [Guid]::Empty,

    [Alias("Limit")]
    [ValidateRange(1, 1000)]
    [int]$PostgreSqlLimit = 100,

    [Alias("h", "Help")]
    [switch]$PostgreSqlHelp
)

$postgresProjectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$postgresVersion = "18.6-1"
$postgresMajorVersion = "18"
$postgresArchiveName = "postgresql-$postgresVersion-windows-x64-binaries.zip"
$postgresDownloadUrl = "https://get.enterprisedb.com/postgresql/$postgresArchiveName"
$postgresArchiveSha256 = "fbe23da234ee31547bf8a36d29dfd81e82b849df2d2b78d2eecb43d360252f8c"
$postgresInstallDirectory = Join-Path $postgresProjectRoot "Tools\PostgreSQL\$postgresVersion"
$postgresBinDirectory = Join-Path $postgresInstallDirectory "bin"
$postgresExecutablePath = Join-Path $postgresBinDirectory "postgres.exe"
$postgresInitDbPath = Join-Path $postgresBinDirectory "initdb.exe"
$postgresControlPath = Join-Path $postgresBinDirectory "pg_ctl.exe"
$postgresPsqlPath = Join-Path $postgresBinDirectory "psql.exe"
$postgresCreateDbPath = Join-Path $postgresBinDirectory "createdb.exe"
$postgresDataDirectory = Join-Path $postgresProjectRoot "Data\PostgreSQL\$postgresMajorVersion"
$postgresLogDirectory = Join-Path $postgresProjectRoot "Logs\PostgreSQL"
$postgresLogPath = Join-Path $postgresLogDirectory "postgresql.log"
$postgresControlOutputPath = Join-Path $postgresLogDirectory "pg_ctl.out.log"
$postgresControlErrorPath = Join-Path $postgresLogDirectory "pg_ctl.err.log"
$postgresPort = 6688
$postgresDatabaseName = "vst_audit"

function Write-PostgreSqlStep {
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Show-PostgreSqlScriptHelp {
    Write-Host @"
Das Holzwerk - PostgreSQL-Verwaltung

Syntax:
  .\Start-VSTPostgreSQL.ps1 [-Action <Start|Status|Stop|DatabaseEntries>]
                                 [-CorrelationId <Guid>] [-Limit <1..1000>] [-h]

Konfiguration:
  Version:    PostgreSQL $postgresVersion
  Adresse:    localhost:$postgresPort
  Datenbank:  $postgresDatabaseName
  Daten:      $postgresDataDirectory
  Log:        $postgresLogPath

Beispiele:
  .\Start-VSTPostgreSQL.ps1
  .\Start-VSTPostgreSQL.ps1 -Action Status
  .\Start-VSTPostgreSQL.ps1 -Action DatabaseEntries -Limit 50
  .\Start-VSTPostgreSQL.ps1 -Action DatabaseEntries -CorrelationId <Guid>
  .\Start-VSTPostgreSQL.ps1 -Action Stop
"@
}

function Test-PostgreSqlTcpPort {
    param(
        [string]$HostName = "localhost",
        [int]$Port = $postgresPort,
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

function Get-PostgreSqlProcess {
    $postmasterPidPath = Join-Path $postgresDataDirectory "postmaster.pid"
    if (-not (Test-Path -LiteralPath $postmasterPidPath)) {
        return $null
    }

    $postmasterPidText = (Get-Content `
        -LiteralPath $postmasterPidPath `
        -TotalCount 1).Trim()
    $postmasterPid = 0
    if (-not [int]::TryParse($postmasterPidText, [ref]$postmasterPid)) {
        return $null
    }

    $process = Get-Process -Id $postmasterPid -ErrorAction SilentlyContinue
    if ($null -eq $process -or $process.ProcessName -ne "postgres") {
        return $null
    }

    return $process
}

function Wait-PostgreSqlPort {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "PostgreSQL wurde vorzeitig mit Exit-Code $($Process.ExitCode) beendet."
        }

        if (Test-PostgreSqlTcpPort) {
            return
        }

        Start-Sleep -Milliseconds 250
    }

    throw "PostgreSQL ist nicht innerhalb von $TimeoutSeconds Sekunden auf Port $postgresPort gestartet."
}

function Wait-PostgreSqlStopped {
    param(
        [Parameter(Mandatory = $true)]
        [int]$ProcessId,
        [int]$TimeoutSeconds = 35
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        $portIsOpen = Test-PostgreSqlTcpPort
        if ($null -eq $process -and -not $portIsOpen) {
            return
        }

        Start-Sleep -Milliseconds 250
    }

    $processStillRuns = $null -ne (
        Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
    $portStillOpen = Test-PostgreSqlTcpPort
    throw (
        "PostgreSQL wurde nicht vollstaendig beendet. " +
        "Prozess aktiv: $processStillRuns; Port $postgresPort offen: $portStillOpen.")
}

function Install-PostgreSql {
    $requiredTools = @(
        $postgresExecutablePath,
        $postgresInitDbPath,
        $postgresControlPath,
        $postgresPsqlPath,
        $postgresCreateDbPath)
    if (@($requiredTools | Where-Object { -not (Test-Path -LiteralPath $_) }).Count -eq 0) {
        return
    }

    if (Test-Path -LiteralPath $postgresInstallDirectory) {
        throw "Die PostgreSQL-Installation ist unvollstaendig: $postgresInstallDirectory"
    }

    Write-PostgreSqlStep "PostgreSQL $postgresVersion installieren"

    $temporaryRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::GetTempPath())
    $temporaryDirectory = Join-Path `
        $temporaryRoot `
        ("vst-postgresql-" + [Guid]::NewGuid().ToString("N"))
    $archivePath = Join-Path $temporaryDirectory $postgresArchiveName
    $extractedDirectory = Join-Path $temporaryDirectory "extracted"
    New-Item -ItemType Directory -Path $extractedDirectory -Force | Out-Null

    try {
        Invoke-WebRequest `
            -Uri $postgresDownloadUrl `
            -OutFile $archivePath `
            -UseBasicParsing

        $actualChecksum = (Get-FileHash `
            -LiteralPath $archivePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualChecksum -ne $postgresArchiveSha256) {
            throw "Die SHA-256-Pruefsumme des PostgreSQL-Archivs ist ungueltig."
        }

        Expand-Archive `
            -LiteralPath $archivePath `
            -DestinationPath $extractedDirectory
        $extractedPostgresDirectory = Join-Path $extractedDirectory "pgsql"
        $extractedExecutable = Join-Path $extractedPostgresDirectory "bin\postgres.exe"
        if (-not (Test-Path -LiteralPath $extractedExecutable)) {
            throw "Das PostgreSQL-Archiv enthaelt keine postgres.exe."
        }

        $postgresInstallRoot = Split-Path -Parent $postgresInstallDirectory
        New-Item -ItemType Directory -Path $postgresInstallRoot -Force | Out-Null
        Move-Item `
            -LiteralPath $extractedPostgresDirectory `
            -Destination $postgresInstallDirectory
    }
    finally {
        $resolvedTemporaryDirectory = [System.IO.Path]::GetFullPath(
            $temporaryDirectory)
        if ($resolvedTemporaryDirectory.StartsWith(
                $temporaryRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item `
                -LiteralPath $resolvedTemporaryDirectory `
                -Recurse `
                -Force `
                -ErrorAction SilentlyContinue
        }
    }

    Write-Host "Installiert: $postgresExecutablePath" -ForegroundColor Green
}

function Initialize-PostgreSqlCluster {
    $versionFilePath = Join-Path $postgresDataDirectory "PG_VERSION"
    if (Test-Path -LiteralPath $versionFilePath) {
        $initializedMajorVersion = (Get-Content `
            -LiteralPath $versionFilePath `
            -Raw).Trim()
        if ($initializedMajorVersion -ne $postgresMajorVersion) {
            throw "Der PostgreSQL-Datencluster verwendet Version $initializedMajorVersion statt $postgresMajorVersion."
        }
        return
    }

    if (Test-Path -LiteralPath $postgresDataDirectory) {
        $existingFiles = @(Get-ChildItem `
            -LiteralPath $postgresDataDirectory `
            -Force `
            -ErrorAction SilentlyContinue)
        if ($existingFiles.Count -gt 0) {
            throw "Das PostgreSQL-Datenverzeichnis ist nicht leer und enthaelt keinen gueltigen Cluster: $postgresDataDirectory"
        }
    }

    Write-PostgreSqlStep "Projektlokalen PostgreSQL-Cluster initialisieren"
    New-Item `
        -ItemType Directory `
        -Path $postgresDataDirectory `
        -Force | Out-Null

    $initDbOutput = & $postgresInitDbPath `
        "--pgdata=$postgresDataDirectory" `
        "--username=postgres" `
        "--encoding=UTF8" `
        "--locale=C" `
        "--auth-host=trust" `
        "--auth-local=trust" 2>&1
    $initDbExitCode = $LASTEXITCODE
    $initDbOutput | ForEach-Object { Write-Host $_ }
    if ($initDbExitCode -ne 0) {
        throw "Der PostgreSQL-Datencluster konnte nicht initialisiert werden."
    }
}

function Stop-PostgreSqlServer {
    if (-not (Test-Path -LiteralPath $postgresControlPath) -or
        -not (Test-Path -LiteralPath (Join-Path $postgresDataDirectory "PG_VERSION"))) {
        return
    }

    $process = Get-PostgreSqlProcess
    if ($null -eq $process) {
        if (Test-PostgreSqlTcpPort) {
            throw (
                "Port $postgresPort ist offen, der projektlokale PostgreSQL-Prozess " +
                "konnte jedoch nicht sicher ermittelt werden. Der fremde Prozess wird nicht beendet.")
        }
        return
    }

    $postgresProcessId = $process.Id

    New-Item -ItemType Directory -Path $postgresLogDirectory -Force | Out-Null
    $controlProcess = Start-Process `
        -FilePath $postgresControlPath `
        -ArgumentList @(
            "stop",
            "-D", ('"{0}"' -f $postgresDataDirectory),
            "-m", "fast",
            "-w",
            "-t", "30") `
        -WorkingDirectory $postgresProjectRoot `
        -RedirectStandardOutput $postgresControlOutputPath `
        -RedirectStandardError $postgresControlErrorPath `
        -WindowStyle Hidden `
        -PassThru
    Wait-Process -Id $controlProcess.Id -Timeout 35 -ErrorAction Stop
    $controlProcess.Refresh()
    $controlExitCode = $controlProcess.ExitCode

    try {
        # Der beobachtete Zustand ist massgeblich: pg_ctl kann unter Windows
        # trotz vollstaendig beendetem Server einen von null abweichenden
        # Exit-Code liefern.
        Wait-PostgreSqlStopped `
            -ProcessId $postgresProcessId `
            -TimeoutSeconds 35
    }
    catch {
        throw (
            "PostgreSQL konnte nicht kontrolliert beendet werden " +
            "(pg_ctl Exit-Code $controlExitCode). $($_.Exception.Message) " +
            "Siehe $postgresControlErrorPath")
    }

    if ($controlExitCode -ne 0) {
        Write-Warning (
            "pg_ctl stop meldete Exit-Code $controlExitCode, PostgreSQL und " +
            "Port $postgresPort wurden jedoch nachweislich beendet.")
    }
}

function Start-PostgreSqlServer {
    $existingProcess = Get-PostgreSqlProcess
    if ($null -ne $existingProcess) {
        Wait-PostgreSqlPort -Process $existingProcess -TimeoutSeconds 30

        return [PSCustomObject]@{
            Name = "PostgreSQL"
            ProcessId = $existingProcess.Id
            StartTimeUtc = $existingProcess.StartTime.ToUniversalTime().ToString("O")
            Port = $postgresPort
        }
    }

    if (Test-PostgreSqlTcpPort) {
        throw "Port $postgresPort ist durch einen fremden Prozess belegt."
    }

    Install-PostgreSql
    Initialize-PostgreSqlCluster
    New-Item -ItemType Directory -Path $postgresLogDirectory -Force | Out-Null

    Write-PostgreSqlStep "PostgreSQL starten"
    $controlProcess = Start-Process `
        -FilePath $postgresControlPath `
        -ArgumentList @(
            "start",
            "-D", ('"{0}"' -f $postgresDataDirectory),
            "-l", ('"{0}"' -f $postgresLogPath),
            "-o", ('"-h localhost -p {0}"' -f $postgresPort),
            "-w",
            "-t", "60") `
        -WorkingDirectory $postgresProjectRoot `
        -RedirectStandardOutput $postgresControlOutputPath `
        -RedirectStandardError $postgresControlErrorPath `
        -WindowStyle Hidden `
        -PassThru
    Wait-Process -Id $controlProcess.Id -Timeout 65 -ErrorAction Stop
    $controlProcess.Refresh()
    $controlExitCode = $controlProcess.ExitCode

    # Auch beim Start entscheidet der tatsaechliche Serverzustand. Das
    # verhindert einen falschen Abbruch, wenn pg_ctl unter Windows einen
    # unzutreffenden Exit-Code liefert, der Postmaster aber bereit ist.
    $process = Get-PostgreSqlProcess
    if ($null -eq $process) {
        throw (
            "PostgreSQL konnte nicht gestartet werden " +
            "(pg_ctl Exit-Code $controlExitCode; kein Postmaster-Prozess). " +
            "Siehe $postgresLogPath und $postgresControlErrorPath")
    }

    try {
        Wait-PostgreSqlPort -Process $process -TimeoutSeconds 30
    }
    catch {
        $startFailure = $_.Exception.Message
        try {
            Stop-PostgreSqlServer
        }
        catch {
            Write-Warning (
                "Der teilweise gestartete PostgreSQL-Prozess konnte nach dem " +
                "Startfehler nicht automatisch beendet werden: $($_.Exception.Message)")
        }
        throw (
            "PostgreSQL konnte nicht gestartet werden " +
            "(pg_ctl Exit-Code $controlExitCode). $startFailure " +
            "Siehe $postgresLogPath und $postgresControlErrorPath")
    }

    if ($controlExitCode -ne 0) {
        Write-Warning (
            "pg_ctl start meldete Exit-Code $controlExitCode, PostgreSQL ist " +
            "auf Port $postgresPort jedoch nachweislich betriebsbereit.")
    }

    try {
        $databaseQuery = "SELECT 1 FROM pg_database WHERE datname = '$postgresDatabaseName';"
        $databaseExists = & $postgresPsqlPath `
            "--host=127.0.0.1" `
            "--port=$postgresPort" `
            "--username=postgres" `
            "--dbname=postgres" `
            "--tuples-only" `
            "--no-align" `
            "--set=ON_ERROR_STOP=1" `
            "--command=$databaseQuery"
        if ($LASTEXITCODE -ne 0) {
            throw "Die PostgreSQL-Datenbanken konnten nicht abgefragt werden."
        }

        if (($databaseExists -join "").Trim() -ne "1") {
            $createDbOutput = & $postgresCreateDbPath `
                "--host=127.0.0.1" `
                "--port=$postgresPort" `
                "--username=postgres" `
                "--encoding=UTF8" `
                $postgresDatabaseName 2>&1
            $createDbExitCode = $LASTEXITCODE
            $createDbOutput | ForEach-Object { Write-Host $_ }
            if ($createDbExitCode -ne 0) {
                throw "Die PostgreSQL-Datenbank '$postgresDatabaseName' konnte nicht erstellt werden."
            }
        }

        Write-Host `
            "Bereit: PostgreSQL auf Port $postgresPort, Datenbank $postgresDatabaseName" `
            -ForegroundColor Green

        return [PSCustomObject]@{
            Name = "PostgreSQL"
            ProcessId = $process.Id
            StartTimeUtc = $process.StartTime.ToUniversalTime().ToString("O")
            Port = $postgresPort
        }
    }
    catch {
        Stop-PostgreSqlServer
        throw
    }
}

function Show-PostgreSqlStatus {
    $process = Get-PostgreSqlProcess
    [PSCustomObject]@{
        Component = "PostgreSQL"
        Version = $postgresVersion
        Port = $postgresPort
        PortStatus = if (Test-PostgreSqlTcpPort) { "offen" } else { "geschlossen" }
        ProcessId = if ($null -eq $process) { "-" } else { $process.Id }
        Database = $postgresDatabaseName
    } | Format-Table -AutoSize
}

function Show-PostgreSqlDatabaseEntries {
    [CmdletBinding()]
    param(
        [Guid]$CorrelationId = [Guid]::Empty,

        [ValidateRange(1, 1000)]
        [int]$Limit = 100
    )

    if (-not (Test-Path -LiteralPath $postgresPsqlPath)) {
        throw (
            "Der PostgreSQL-Client wurde nicht gefunden: $postgresPsqlPath. " +
            "PostgreSQL muss mindestens einmal installiert worden sein.")
    }

    $postgresProcess = Get-PostgreSqlProcess
    if ($null -eq $postgresProcess) {
        if (Test-PostgreSqlTcpPort) {
            throw (
                "Port $postgresPort ist offen, gehoert aber nicht zum " +
                "projektlokalen PostgreSQL-Cluster. Die Abfrage wurde abgebrochen.")
        }

        throw (
            "Der projektlokale PostgreSQL-Cluster laeuft nicht. " +
            "Starten Sie ihn mit '.\Start-VSTPostgreSQL.ps1 -Action Start'.")
    }

    if (-not (Test-PostgreSqlTcpPort)) {
        throw "PostgreSQL antwortet nicht auf 127.0.0.1:$postgresPort."
    }

    $correlationFilter = if ($CorrelationId -eq [Guid]::Empty) {
        ""
    }
    else {
        "WHERE correlation_id = '$($CorrelationId.ToString('D'))'::uuid"
    }
    $databaseEntriesQuery = @"
SELECT
    selected.sequence_number AS "Sequenz",
    to_char(
        selected.occurred_at AT TIME ZONE 'UTC',
        'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"') AS "Zeit (UTC)",
    selected.correlation_id AS "Correlation-ID",
    selected.event_id AS "Event-ID",
    selected.previous_event_id AS "Vorgaenger-ID",
    selected.responsible_service AS "Service",
    selected.event_type AS "Event-Typ",
    selected.status_code AS "Status",
    selected.actor AS "Akteur",
    jsonb_pretty(selected.payload) AS "Payload"
FROM (
    SELECT *
    FROM public.audit_snapshots
    $correlationFilter
    ORDER BY sequence_number DESC
    LIMIT $Limit
) AS selected
ORDER BY selected.sequence_number ASC;
"@

    $filterDescription = if ($CorrelationId -eq [Guid]::Empty) {
        "ohne Correlation-ID-Filter"
    }
    else {
        "fuer Correlation-ID $($CorrelationId.ToString('D'))"
    }
    Write-PostgreSqlStep (
        "Bis zu $Limit Audit-Datenbankeintraege $filterDescription lesen")

    $queryOutput = & $postgresPsqlPath `
        "--host=127.0.0.1" `
        "--port=$postgresPort" `
        "--username=postgres" `
        "--dbname=$postgresDatabaseName" `
        "--no-password" `
        "--set=ON_ERROR_STOP=1" `
        "--pset=pager=off" `
        "--expanded" `
        "--command=$databaseEntriesQuery" 2>&1
    $queryExitCode = $LASTEXITCODE
    if ($queryExitCode -ne 0) {
        $queryError = ($queryOutput | Out-String).Trim()
        throw (
            "Die Tabelle public.audit_snapshots konnte nicht gelesen werden " +
            "(psql Exit-Code $queryExitCode). $queryError")
    }

    $queryOutput | ForEach-Object { Write-Host $_ }
}

if ($MyInvocation.InvocationName -eq ".") {
    return
}

$ErrorActionPreference = "Stop"

if ($PostgreSqlHelp) {
    Show-PostgreSqlScriptHelp
    return
}

switch ($PostgreSqlAction) {
    "Start" {
        $null = Start-PostgreSqlServer
    }
    "Status" {
        Show-PostgreSqlStatus
    }
    "DatabaseEntries" {
        Show-PostgreSqlDatabaseEntries `
            -CorrelationId $PostgreSqlCorrelationId `
            -Limit $PostgreSqlLimit
    }
    "Stop" {
        Write-PostgreSqlStep "PostgreSQL beenden"
        $process = Get-PostgreSqlProcess
        if ($null -eq $process) {
            Write-Host "PostgreSQL laeuft nicht."
            return
        }

        Write-Host "Stoppe PostgreSQL (PID $($process.Id)) ..."
        Stop-PostgreSqlServer
        Write-Host "PostgreSQL wurde kontrolliert beendet." -ForegroundColor Green
    }
}
