<#
.SYNOPSIS
    Initialisiert, startet, prueft und beendet Das Holzwerk.

.DESCRIPTION
    Start fuehrt einen Restore und Build aus, startet alle benoetigten Prozesse
    in Abhaengigkeitsreihenfolge, prueft den vollstaendigen API-Pfad und oeffnet
    anschliessend die Website im Standardbrowser.

.PARAMETER Action
    Start (Standard), Status oder Stop.

.PARAMETER SkipBuild
    Ueberspringt Restore und Build, wenn bereits aktuelle Build-Ausgaben
    vorhanden sind.

.PARAMETER NoBrowser
    Startet und prueft die Anwendung, ohne den Browser zu oeffnen.

.EXAMPLE
    .\Start-VSTOnlineStore.ps1

.EXAMPLE
    .\Start-VSTOnlineStore.ps1 -Action Status

.EXAMPLE
    .\Start-VSTOnlineStore.ps1 -Action Stop
#>

[CmdletBinding()]
param(
    [ValidateSet("Start", "Status", "Stop")]
    [string]$Action = "Start",

    [switch]$SkipBuild,

    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"

$projectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$solutionPath = Join-Path $projectRoot "VST_OnlineStore.slnx"
$runtimeDirectory = Join-Path $projectRoot "Logs\Startup"
$processManifestPath = Join-Path $runtimeDirectory "holzwerk-processes.json"
$websiteUrl = "http://localhost:5275/"
$apiReadinessUrl = "http://localhost:5275/api/products/featured"
$browserUrl = "{0}?version=3&started={1}" -f `
    $websiteUrl.TrimEnd("/"), `
    [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

$serviceDefinitions = @(
    [PSCustomObject]@{
        Name = "StoreBackend"
        ProjectDirectory = "StoreBackend"
        Assembly = "StoreBackend.dll"
        Port = 6667
    },
    [PSCustomObject]@{
        Name = "WarehouseService"
        ProjectDirectory = "Services\WarehouseService"
        Assembly = "WarehouseService.dll"
        Port = 6669
    },
    [PSCustomObject]@{
        Name = "BillingService"
        ProjectDirectory = "Services\BillingService"
        Assembly = "BillingService.dll"
        Port = 6670
    },
    [PSCustomObject]@{
        Name = "InvoiceService"
        ProjectDirectory = "Services\InvoiceService"
        Assembly = "InvoiceService.dll"
        Port = 6671
    },
    [PSCustomObject]@{
        Name = "AuditService"
        ProjectDirectory = "Services\AuditService"
        Assembly = "AuditService.dll"
        Port = 6672
    },
    [PSCustomObject]@{
        Name = "ShopService"
        ProjectDirectory = "Services\ShopService"
        Assembly = "ShopService.dll"
        Port = 6668
    },
    [PSCustomObject]@{
        Name = "StoreProxy"
        ProjectDirectory = "StoreProxy"
        Assembly = "StoreProxy.dll"
        Port = 5275
    }
)

function Write-Step {
    param([string]$Message)

    Write-Host "`n==> $Message" -ForegroundColor Cyan
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

function Test-ManifestProcess {
    param([Parameter(Mandatory = $true)][object]$Entry)

    $process = Get-Process -Id $Entry.ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return $false
    }

    try {
        $expectedStart = [DateTime]::Parse($Entry.StartTimeUtc).ToUniversalTime()
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

    $rows | Format-Table -AutoSize
}

function Stop-Application {
    Write-Step "Das Holzwerk beenden"

    $entries = @(Get-ManifestEntries)
    if ($entries.Count -eq 0) {
        Write-Host "Keine vom Startskript verwalteten Prozesse gefunden."
        Show-ApplicationStatus
        return
    }

    [Array]::Reverse($entries)
    foreach ($entry in $entries) {
        if (-not (Test-ManifestProcess -Entry $entry)) {
            Write-Host "Uebersprungen: $($entry.Name) laeuft nicht mehr."
            continue
        }

        Write-Host "Stoppe $($entry.Name) (PID $($entry.ProcessId)) ..."
        Stop-Process -Id $entry.ProcessId -Force
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

function Wait-ApplicationApi {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        try {
            $products = Invoke-RestMethod -Uri $apiReadinessUrl -Method Get -TimeoutSec 5
            $productCount = [int]$products.Count
            if ($productCount -lt 1 -and $null -ne $products) {
                $productCount = 1
            }
            $website = Invoke-WebRequest -Uri $websiteUrl -UseBasicParsing -TimeoutSec 5
            $hasCurrentWebsite = `
                $website.Content.Contains("<title>Das Holzwerk</title>") -and `
                $website.Content.Contains('id="open-cart"') -and `
                $website.Content.Contains("Holzwerk DemoPay")

            if ($website.StatusCode -eq 200 -and
                $productCount -gt 0 -and
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

    $occupiedServices = @($serviceDefinitions | Where-Object { Test-TcpPort -Port $_.Port })
    if ($occupiedServices.Count -gt 0) {
        $occupiedDescription = ($occupiedServices | ForEach-Object { "$($_.Name):$($_.Port)" }) -join ", "
        throw "Benoetigte Ports sind bereits belegt: $occupiedDescription. Bitte zuerst '.\Start-VSTOnlineStore.ps1 -Action Stop' ausfuehren oder die fremden Prozesse beenden."
    }

    New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null

    if (-not $SkipBuild) {
        Write-Step "NuGet-Pakete wiederherstellen"
        Invoke-DotNetCommand -Arguments @("restore", $solutionPath)

        Write-Step "Solution bauen"
        Invoke-DotNetCommand -Arguments @("build", $solutionPath, "--no-restore")
    }

    Write-Step "Backend, Services und Proxy starten"
    $startedProcesses = @()
    try {
        foreach ($service in $serviceDefinitions) {
            $startedProcesses += Start-ApplicationProcess -Service $service
        }

        $startedProcesses | ConvertTo-Json | Set-Content -LiteralPath $processManifestPath -Encoding UTF8

        Write-Step "Vollstaendigen Aufrufpfad pruefen"
        $productCount = Wait-ApplicationApi
        Write-Host "$productCount Produkte sowie Branding, Warenkorb und Payment-Provider erfolgreich ueber den Proxy geladen." -ForegroundColor Green

        if (-not $NoBrowser) {
            Write-Step "Website oeffnen"
            Start-Process -FilePath $browserUrl
        }

        Write-Host "`nDas Holzwerk ist bereit: $websiteUrl" -ForegroundColor Green
        Write-Host "Status: .\Start-VSTOnlineStore.ps1 -Action Status"
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
}
