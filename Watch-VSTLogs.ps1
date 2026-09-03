<#
.SYNOPSIS
    Zeigt die Konsolenausgaben des Stores in einer gemeinsamen Live-Ansicht.
.DESCRIPTION
    Liest nur Logs/Startup/*.out.log, *.err.log und Logs/PostgreSQL/*.log.
    Bestehende Dateien werden mit einem kurzen Rueckblick angezeigt; danach
    folgen neue Zeilen. Das Schliessen der Ansicht beendet keine Services.
.PARAMETER LogDirectory
    Logverzeichnis des Stores; standardmaessig Logs neben diesem Subscript.
.PARAMETER Tail
    Anzahl der anfangs angezeigten Zeilen pro Datei; Standard 20.
.PARAMETER Once
    Einmal lesen und beenden, statt neue Meldungen fortlaufend anzuzeigen.
#>
[CmdletBinding()]
param(
    [string]$LogDirectory,
    [ValidateRange(0, 1000)]
    [int]$Tail = 20,
    [switch]$Once
)

function Get-VstLogSources {
    param([string]$Directory)

    foreach ($folder in @("Startup", "PostgreSQL")) {
        $path = Join-Path $Directory $folder
        if (-not (Test-Path -LiteralPath $path)) { continue }
        Get-ChildItem -LiteralPath $path -Filter "*.log" -File |
            Where-Object { $folder -eq "PostgreSQL" -or $_.Name -match '\.(out|err)\.log$' } |
            Sort-Object Name |
            ForEach-Object {
                [PSCustomObject]@{
                    Path = $_.FullName
                    Source = if ($folder -eq "PostgreSQL") {
                        "PostgreSQL/$($_.Name)"
                    } else { $_.Name -replace '\.log$', '' }
                    IsError = $_.Name -like "*.err.log"
                }
            }
    }
}

function Get-VstLogTailOffset {
    param([IO.FileStream]$Stream, [int]$LineCount)

    if ($LineCount -lt 0) { return [long]0 }
    if ($LineCount -eq 0) { return $Stream.Length }
    $buffer = New-Object byte[] 8192
    $position = $Stream.Length
    $remaining = $LineCount
    while ($position -gt 0) {
        $count = [int][Math]::Min($buffer.Length, $position)
        $position -= $count
        $Stream.Position = $position
        $read = $Stream.Read($buffer, 0, $count)
        for ($index = $read - 1; $index -ge 0; $index--) {
            if ($buffer[$index] -eq 10 -and
                ($position + $index) -lt ($Stream.Length - 1)) {
                $remaining--
                if ($remaining -eq 0) { return ($position + $index + 1) }
            }
        }
    }
    return [long]0
}

function Read-VstLogUpdates {
    param([object]$Source, [hashtable]$States, [int]$InitialTail = -1)

    $stream = $null
    try {
        # Dateien nie exklusiv halten: Schreiben, Neustart und Rotation bleiben moeglich.
        $stream = [IO.File]::Open($Source.Path, [IO.FileMode]::Open,
            [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $state = $States[$Source.Path]
        if ($null -ne $state) {
            $reset = $stream.Length -lt $state.Offset
            if (-not $reset -and $state.Guard.Length -gt 0) {
                $stream.Position = $state.Offset - $state.Guard.Length
                $guard = New-Object byte[] $state.Guard.Length
                $read = $stream.Read($guard, 0, $guard.Length)
                $reset = $read -ne $guard.Length -or
                    [Convert]::ToBase64String($guard) -ne [Convert]::ToBase64String($state.Guard)
            }
            if ($reset) {
                $States.Remove($Source.Path)
                $state = $null
                $InitialTail = -1
            }
        }
        if ($null -eq $state) {
            $state = @{
                Offset = Get-VstLogTailOffset -Stream $stream -LineCount $InitialTail
                Decoder = [Text.Encoding]::UTF8.GetDecoder()
                Pending = ""
                Guard = [byte[]]@()
            }
            $States[$Source.Path] = $state
        }

        $stream.Position = $state.Offset
        # Pro Durchlauf begrenzen, damit ein sehr aktiver Service andere nicht verdraengt.
        $buffer = New-Object byte[] 65536
        $count = $stream.Read($buffer, 0, $buffer.Length)
        if ($count -gt 0) {
            $characters = New-Object char[] ($count + 2)
            $length = $state.Decoder.GetChars($buffer, 0, $count, $characters, 0, $false)
            $text = $state.Pending + [string]::new($characters, 0, $length)
            $parts = $text -split "`n"
            for ($index = 0; $index -lt $parts.Length - 1; $index++) {
                [PSCustomObject]@{
                    Source = $Source.Source
                    Text = $parts[$index].TrimEnd([char]13).TrimStart([char]0xFEFF)
                    IsError = $Source.IsError
                }
            }
            # Eine erst teilweise geschriebene Zeile erst nach ihrem Zeilenende ausgeben.
            $state.Pending = $parts[-1]
            $state.Offset += $count
        }
        $guardLength = [int][Math]::Min(64, $state.Offset)
        $state.Guard = New-Object byte[] $guardLength
        $stream.Position = $state.Offset - $guardLength
        $null = $stream.Read($state.Guard, 0, $guardLength)
    }
    catch [IO.IOException] {
        # Kurzzeitig gesperrte, geloeschte oder rotierende Dateien spaeter erneut lesen.
        if (-not (Test-Path -LiteralPath $Source.Path)) { $States.Remove($Source.Path) }
    }
    catch [UnauthorizedAccessException] {
        Write-Warning "Logdatei kann nicht gelesen werden: $($Source.Path)"
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
    }
}

function Watch-VstLogFiles {
    param([string]$Directory, [int]$InitialTail = 20, [switch]$SinglePass)

    $states = @{}
    $firstPass = $true
    do {
        $sources = @(Get-VstLogSources -Directory $Directory)
        foreach ($knownPath in @($states.Keys)) {
            if ($knownPath -notin $sources.Path) { $states.Remove($knownPath) }
        }
        foreach ($source in $sources) {
            $tailCount = if ($firstPass) { $InitialTail } else { -1 }
            Read-VstLogUpdates -Source $source -States $states -InitialTail $tailCount |
                ForEach-Object {
                    $color = if ($_.Text -match '(?i)\b(ERROR|FATAL|PANIC)\b|^\s*(fail|crit):') {
                        "Red"
                    } elseif ($_.IsError -or $_.Text -match '(?i)\bWARN(ING)?\b') {
                        "Yellow"
                    } else { "Gray" }
                    Write-Host ("[{0}] {1}" -f $_.Source, $_.Text) -ForegroundColor $color
                }
        }
        $firstPass = $false
        if (-not $SinglePass) { Start-Sleep -Milliseconds 500 }
    } while (-not $SinglePass)
}

if ($MyInvocation.InvocationName -eq ".") { return }
$ErrorActionPreference = "Stop"
# Bei Windows PowerShell 5.1 ist PSScriptRoot beim direkten Aufruf mit -File
# in Parameter-Standardwerten noch leer. Erst im Skriptkoerper aufloesen.
if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
    $LogDirectory = Join-Path $PSScriptRoot "Logs"
}
try { $Host.UI.RawUI.WindowTitle = "VST OnlineStore - Live-Logs" } catch { }
Write-Host "VST OnlineStore - Live-Logs" -ForegroundColor Cyan
Write-Host "Letzte $Tail Zeilen je Datei, danach neue Meldungen (Aktualisierung: 0,5 s)."
Write-Host "Quelle: $LogDirectory"
Write-Host "Fenster schliessen oder Strg+C beendet nur diese Ansicht, nicht den Store."
Write-Host "RabbitMQ-Logs bleiben beim extern betriebenen Broker.`n"
Watch-VstLogFiles -Directory $LogDirectory -InitialTail $Tail -SinglePass:$Once
