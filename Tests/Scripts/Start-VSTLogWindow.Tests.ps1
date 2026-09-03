# Isolierte Start-/Stop-Tests: Prozess- und Serviceaktionen werden ersetzt.
$ErrorActionPreference = "Stop"
$logTestProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
. (Join-Path $logTestProjectRoot "Start-VSTOnlineStore.ps1") -Help 6>$null
$runtimeDirectory = Join-Path ([IO.Path]::GetTempPath()) ("vst-log-start-test-" + [Guid]::NewGuid().ToString("N"))
$logWindowStatePath = Join-Path $runtimeDirectory "log-window-process.json"
$processManifestPath = Join-Path $runtimeDirectory "unused-manifest.json"
$script:logTestCalls = @()
$script:logTestWindowFailure = $false

function Assert-StartLogTest {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Logfenster-Integrationstest fehlgeschlagen: $Message" }
}
function Assert-RabbitMqAvailable { }
function Test-TcpPort { return $false }
function Save-ManifestEntries { }
function Get-ManifestEntries { return @() }
function Start-PostgreSqlServer { return [PSCustomObject]@{ Name = "PostgreSQL"; ProcessId = -1 } }
function Get-PostgreSqlProcess { return [PSCustomObject]@{ Id = -1 } }
function Stop-PostgreSqlServer { $script:logTestCalls += "stop-postgresql" }
function Start-ApplicationProcess {
    param([object]$Service)
    return [PSCustomObject]@{ Name = $Service.Name; ProcessId = -1 }
}
function Wait-ApplicationApi { $script:logTestCalls += "ready"; return 1 }
function Start-LogWindow {
    $script:logTestCalls += "open-window"
    if ($script:logTestWindowFailure) { throw "simulierter Fensterstartfehler" }
    return [PSCustomObject]@{ Name = "LogWindow"; ProcessId = -1 }
}
function Stop-LogWindow { $script:logTestCalls += "close-window" }
function Stop-ManagedEntry {
    param([object]$Entry, [switch]$SuppressErrors)
    $script:logTestCalls += "stop-$($Entry.Name)"
}
function Start-Process { throw "simulierter Browserfehler" }

try {
    $SkipBuild = $true
    $SkipCollector = $true
    $NoBrowser = $true
    $NoLogWindow = $false
    Start-Application 6>$null
    Assert-StartLogTest (($script:logTestCalls -join ',') -eq "ready,open-window") "Fenster oeffnet nach erfolgreicher Betriebspruefung."

    $script:logTestCalls = @()
    $NoLogWindow = $true
    Start-Application 6>$null
    Assert-StartLogTest (($script:logTestCalls -join ',') -eq "ready") "NoLogWindow unterdrueckt das Fenster."

    $script:logTestCalls = @()
    $NoLogWindow = $false
    $script:logTestWindowFailure = $true
    Start-Application 3>$null 6>$null
    Assert-StartLogTest (($script:logTestCalls -join ',') -eq "ready,open-window") "Fensterfehler beendet keine Services."

    $script:logTestCalls = @()
    $script:logTestWindowFailure = $false
    $NoBrowser = $false
    $failed = $false
    try { Start-Application 6>$null } catch { $failed = $true }
    Assert-StartLogTest $failed "Simulierter spaeter Startfehler wird weitergereicht."
    Assert-StartLogTest ($script:logTestCalls -contains "stop-LogWindow") "Ein neu gestartetes Fenster wird beim Start-Rollback geschlossen."

    $script:logTestCalls = @()
    Stop-Application 6>$null
    Assert-StartLogTest (($script:logTestCalls -join ',') -eq "stop-postgresql,close-window") "Stop schliesst das Fenster nach PostgreSQL."
    Write-Output "Logfenster-Integrationstests erfolgreich: Start, NoLogWindow, Fehlerisolation, Rollback und Stop."
}
finally {
    if (Test-Path -LiteralPath $runtimeDirectory) {
        Remove-Item -LiteralPath $runtimeDirectory
    }
}
