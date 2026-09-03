<#
.SYNOPSIS
    OpenTelemetry-Subscript für den projektlokalen Collector.

.DESCRIPTION
    Laedt den OpenTelemetry Collector beim ersten Start als geprueftes
    Windows-Binaerarchiv, startet ihn mit der Projektkonfiguration und verwaltet
    seinen Prozess auf Port 6687. Das Subscript kann eigenstaendig ausgefuehrt
    oder vom Start-Skript des OnlineStores eingebunden werden.

.PARAMETER OpenTelemetryAction
    Start (Standard), Status oder Stop. Kann ueber den kurzen Parameternamen
    -Action angegeben werden.

.PARAMETER OpenTelemetryHelp
    Zeigt ueber -h oder -Help eine kompakte Uebersicht und Beispiele.

.EXAMPLE
    .\Start-VSTOpenTelemetryCollector.ps1

.EXAMPLE
    .\Start-VSTOpenTelemetryCollector.ps1 -Action Status

.EXAMPLE
    .\Start-VSTOpenTelemetryCollector.ps1 -Action Stop
#>

[CmdletBinding()]
param(
    [Alias("Action")]
    [ValidateSet("Start", "Status", "Stop")]
    [string]$OpenTelemetryAction = "Start",

    [Alias("h", "Help")]
    [switch]$OpenTelemetryHelp
)

$collectorProjectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$collectorRuntimeDirectory = Join-Path $collectorProjectRoot "Logs\Startup"
$collectorProcessStatePath = Join-Path $collectorRuntimeDirectory "opentelemetry-collector-process.json"
$collectorLogDirectory = Join-Path $collectorProjectRoot "Logs\OpenTelemetry"
$collectorVersion = "0.157.0"
$collectorArchiveName = "otelcol-contrib_${collectorVersion}_windows_amd64.tar.gz"
$collectorReleaseBaseUrl = "https://github.com/open-telemetry/opentelemetry-collector-releases/releases/download/v$collectorVersion"
$collectorInstallDirectory = Join-Path $collectorProjectRoot "Tools\OpenTelemetryCollector\$collectorVersion"
$collectorExecutablePath = Join-Path $collectorInstallDirectory "otelcol-contrib.exe"
$collectorConfigPath = Join-Path $collectorProjectRoot "Observability\otel-collector-config.yaml"
$collectorStandardOutputPath = Join-Path $collectorRuntimeDirectory "OpenTelemetryCollector.out.log"
$collectorStandardErrorPath = Join-Path $collectorRuntimeDirectory "OpenTelemetryCollector.err.log"
$collectorPort = 6687

function Write-OpenTelemetryStep {
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Show-OpenTelemetryScriptHelp {
    Write-Host @"
Das Holzwerk - OpenTelemetry-Subscript

Syntax:
  .\Start-VSTOpenTelemetryCollector.ps1 [-Action <Start|Status|Stop>] [-h]

Konfiguration:
  Version:       $collectorVersion
  OTLP/gRPC:     localhost:$collectorPort
  Konfiguration: $collectorConfigPath
  Log:           $(Join-Path $collectorLogDirectory "vst-online-store.jsonl")

Beispiele:
  .\Start-VSTOpenTelemetryCollector.ps1
  .\Start-VSTOpenTelemetryCollector.ps1 -Action Status
  .\Start-VSTOpenTelemetryCollector.ps1 -Action Stop
"@
}

function Test-OpenTelemetryTcpPort {
    param(
        [string]$HostName = "localhost",
        [int]$Port = $collectorPort,
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

function Test-OpenTelemetryProcessEntry {
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

function Get-OpenTelemetryProcessEntry {
    if (-not (Test-Path -LiteralPath $collectorProcessStatePath)) {
        return $null
    }

    try {
        $content = Get-Content -LiteralPath $collectorProcessStatePath -Raw
        if ([string]::IsNullOrWhiteSpace($content)) {
            return $null
        }

        $entry = ConvertFrom-Json $content
        if (-not (Test-OpenTelemetryProcessEntry -Entry $entry)) {
            return $null
        }

        return $entry
    }
    catch {
        return $null
    }
}

function Save-OpenTelemetryProcessEntry {
    param([Parameter(Mandatory = $true)][object]$Entry)

    New-Item -ItemType Directory -Path $collectorRuntimeDirectory -Force | Out-Null
    ConvertTo-Json -InputObject $Entry |
        Set-Content -LiteralPath $collectorProcessStatePath -Encoding UTF8
}

function Wait-OpenTelemetryPort {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Der OpenTelemetry Collector wurde vorzeitig mit Exit-Code $($Process.ExitCode) beendet."
        }

        if (Test-OpenTelemetryTcpPort) {
            return
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Der OpenTelemetry Collector ist nicht innerhalb von $TimeoutSeconds Sekunden auf Port $collectorPort gestartet."
}

function Install-OpenTelemetryCollector {
    if (Test-Path -LiteralPath $collectorExecutablePath) {
        return
    }

    Write-OpenTelemetryStep "OpenTelemetry Collector $collectorVersion installieren"

    if ($null -eq (Get-Command tar -ErrorAction SilentlyContinue)) {
        throw "Das Windows-Werkzeug 'tar' wurde nicht gefunden."
    }

    $temporaryRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::GetTempPath())
    $temporaryDirectory = Join-Path `
        $temporaryRoot `
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
            throw "Die offizielle Pruefsumme fuer $collectorArchiveName wurde nicht gefunden."
        }

        $actualChecksum = (Get-FileHash `
            -LiteralPath $archivePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualChecksum -ne $expectedChecksum) {
            throw "Die SHA-256-Pruefsumme des Collector-Archivs ist ungueltig."
        }

        & tar -xzf $archivePath -C $extractedDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "Das Collector-Archiv konnte nicht entpackt werden."
        }

        $extractedExecutable = Join-Path $extractedDirectory "otelcol-contrib.exe"
        if (-not (Test-Path -LiteralPath $extractedExecutable)) {
            throw "Das Collector-Archiv enthaelt keine otelcol-contrib.exe."
        }

        $collectorInstallRoot = Split-Path -Parent $collectorInstallDirectory
        New-Item -ItemType Directory -Path $collectorInstallRoot -Force | Out-Null
        Move-Item `
            -LiteralPath $extractedDirectory `
            -Destination $collectorInstallDirectory
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

    Write-Host "Installiert: $collectorExecutablePath" -ForegroundColor Green
}

function Start-OpenTelemetryCollector {
    if (Test-OpenTelemetryTcpPort) {
        $existingEntry = Get-OpenTelemetryProcessEntry
        if ($null -eq $existingEntry) {
            throw "Port $collectorPort ist durch einen fremden Prozess belegt."
        }

        return $existingEntry
    }

    Install-OpenTelemetryCollector
    if (-not (Test-Path -LiteralPath $collectorConfigPath)) {
        throw "Collector-Konfiguration nicht gefunden: $collectorConfigPath"
    }

    New-Item -ItemType Directory -Path $collectorLogDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $collectorRuntimeDirectory -Force | Out-Null
    $process = Start-Process `
        -FilePath $collectorExecutablePath `
        -ArgumentList @("--config", ('"{0}"' -f $collectorConfigPath)) `
        -WorkingDirectory $collectorProjectRoot `
        -RedirectStandardOutput $collectorStandardOutputPath `
        -RedirectStandardError $collectorStandardErrorPath `
        -WindowStyle Hidden `
        -PassThru

    try {
        Wait-OpenTelemetryPort -Process $process -TimeoutSeconds 30
        $entry = [PSCustomObject]@{
            Name = "OpenTelemetryCollector"
            ProcessId = $process.Id
            StartTimeUtc = $process.StartTime.ToUniversalTime().ToString("O")
            Port = $collectorPort
        }
        Save-OpenTelemetryProcessEntry -Entry $entry
        Write-Host `
            "Bereit: OpenTelemetry Collector auf Port $collectorPort" `
            -ForegroundColor Green
        return $entry
    }
    catch {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        Remove-Item `
            -LiteralPath $collectorProcessStatePath `
            -Force `
            -ErrorAction SilentlyContinue
        throw
    }
}

function Stop-OpenTelemetryCollector {
    param([object]$Entry)

    $targetEntry = if ($null -ne $Entry) {
        $Entry
    }
    else {
        Get-OpenTelemetryProcessEntry
    }

    try {
        if ($null -ne $targetEntry -and
            (Test-OpenTelemetryProcessEntry -Entry $targetEntry)) {
            Stop-Process -Id $targetEntry.ProcessId -Force
            Wait-Process `
                -Id $targetEntry.ProcessId `
                -Timeout 10 `
                -ErrorAction SilentlyContinue
        }
    }
    finally {
        Remove-Item `
            -LiteralPath $collectorProcessStatePath `
            -Force `
            -ErrorAction SilentlyContinue
    }
}

function Show-OpenTelemetryStatus {
    $entry = Get-OpenTelemetryProcessEntry
    [PSCustomObject]@{
        Component = "OpenTelemetryCollector"
        Version = $collectorVersion
        Port = $collectorPort
        PortStatus = if (Test-OpenTelemetryTcpPort) { "offen" } else { "geschlossen" }
        ProcessId = if ($null -eq $entry) { "-" } else { $entry.ProcessId }
        Configuration = $collectorConfigPath
    } | Format-Table -AutoSize
}

if ($MyInvocation.InvocationName -eq ".") {
    return
}

$ErrorActionPreference = "Stop"

if ($OpenTelemetryHelp) {
    Show-OpenTelemetryScriptHelp
    return
}

switch ($OpenTelemetryAction) {
    "Start" {
        $null = Start-OpenTelemetryCollector
    }
    "Status" {
        Show-OpenTelemetryStatus
    }
    "Stop" {
        Write-OpenTelemetryStep "OpenTelemetry Collector beenden"
        $entry = Get-OpenTelemetryProcessEntry
        if ($null -eq $entry) {
            Stop-OpenTelemetryCollector
            Write-Host "Der OpenTelemetry Collector laeuft nicht."
            return
        }

        Write-Host "Stoppe OpenTelemetry Collector (PID $($entry.ProcessId)) ..."
        Stop-OpenTelemetryCollector -Entry $entry
        Write-Host `
            "Der OpenTelemetry Collector wurde beendet." `
            -ForegroundColor Green
    }
}
