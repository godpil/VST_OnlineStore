# Ohne zusaetzliche Testpakete: prueft nur temporaere Logdateien, keine Services.
$ErrorActionPreference = "Stop"
$logTestProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
. (Join-Path $logTestProjectRoot "Watch-VSTLogs.ps1")

function Assert-LogTest {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Live-Log-Test fehlgeschlagen: $Message" }
}

$logTestRoot = Join-Path ([IO.Path]::GetTempPath()) ("vst-live-log-test-" + [Guid]::NewGuid().ToString("N"))
$logTestFiles = @()
$utf8 = [Text.UTF8Encoding]::new($false)
try {
    $startupDirectory = Join-Path $logTestRoot "Startup"
    $postgresDirectory = Join-Path $logTestRoot "PostgreSQL"
    New-Item -ItemType Directory -Path $startupDirectory, $postgresDirectory | Out-Null
    $serviceLog = Join-Path $startupDirectory "ShopService.out.log"
    $collectorLog = Join-Path $startupDirectory "OpenTelemetryCollector.err.log"
    $postgresLog = Join-Path $postgresDirectory "postgresql.log"
    $ignoredLog = Join-Path $startupDirectory "unrelated.log"
    $logTestFiles = @($serviceLog, $collectorLog, $postgresLog, $ignoredLog)
    [IO.File]::WriteAllText($serviceLog, "first`r`nsecond`r`n", $utf8)
    [IO.File]::WriteAllText($collectorLog, "collector ready`n", $utf8)
    [IO.File]::WriteAllText($postgresLog, "database ready`n", $utf8)
    [IO.File]::WriteAllText($ignoredLog, "not a console log`n", $utf8)

    $sources = @(Get-VstLogSources -Directory $logTestRoot)
    Assert-LogTest ($sources.Count -eq 3) "Service, Collector und PostgreSQL werden erkannt."
    Assert-LogTest (@(Get-VstLogSources -Directory (Join-Path $logTestRoot "absent")).Count -eq 0) "Fehlende Ordner sind erlaubt."
    $source = $sources | Where-Object { $_.Path -eq $serviceLog }
    $states = @{}
    $lines = @(Read-VstLogUpdates -Source $source -States $states -InitialTail 1)
    Assert-LogTest ($lines.Count -eq 1 -and $lines[0].Text -eq "second") "Anfangs nur die letzte Zeile."
    Assert-LogTest (@(Read-VstLogUpdates -Source $source -States $states).Count -eq 0) "Keine Wiederholung bereits gelesener Zeilen."

    # Ein UTF-8-Zeichen sowie eine Zeile duerfen ueber mehrere Schreibvorgaenge verteilt sein.
    $unicodeMessage = "gr" + [char]0x00F6 + [char]0x00DF + "er"
    $bytes = $utf8.GetBytes($unicodeMessage + "`r`n")
    $writer = [IO.File]::Open($serviceLog, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
    try {
        $writer.Write($bytes, 0, 3)
        $writer.Flush()
        Assert-LogTest (@(Read-VstLogUpdates -Source $source -States $states).Count -eq 0) "Unvollstaendige Zeilen werden gepuffert."
        $writer.Write($bytes, 3, $bytes.Length - 3)
        $writer.Flush()
        $lines = @(Read-VstLogUpdates -Source $source -States $states)
        Assert-LogTest ($lines.Count -eq 1 -and $lines[0].Text -eq $unicodeMessage) "UTF-8 bleibt bei Teilwrites erhalten."
    }
    finally { $writer.Dispose() }

    [IO.File]::WriteAllText($serviceLog, "r`n", $utf8)
    $lines = @(Read-VstLogUpdates -Source $source -States $states)
    Assert-LogTest ($lines.Count -eq 1 -and $lines[0].Text -eq "r") "Verkuerzte Datei wird erneut gelesen."
    [IO.File]::WriteAllText($serviceLog, "replacement line longer than before`nnext`n", $utf8)
    $lines = @(Read-VstLogUpdates -Source $source -States $states)
    Assert-LogTest ($lines.Count -eq 2 -and $lines[0].Text -eq "replacement line longer than before") "Auch laengere neu geschriebene Datei wird erkannt."

    $states = @{}
    Assert-LogTest (@(Read-VstLogUpdates -Source $source -States $states -InitialTail 0).Count -eq 0) "Tail 0 ueberspringt bestehende Zeilen."
    [IO.File]::AppendAllText($serviceLog, "live message`n", $utf8)
    $lines = @(Read-VstLogUpdates -Source $source -States $states)
    Assert-LogTest ($lines.Count -eq 1 -and $lines[0].Text -eq "live message") "Neue Zeilen werden nach Tail 0 geliefert."

    $lock = [IO.File]::Open($serviceLog, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Assert-LogTest (@(Read-VstLogUpdates -Source $source -States $states).Count -eq 0) "Dateisperre beendet die Ansicht nicht."
    }
    finally { $lock.Dispose() }
    [IO.File]::AppendAllText($serviceLog, "after lock`n", $utf8)
    Assert-LogTest (@(Read-VstLogUpdates -Source $source -States $states)[0].Text -eq "after lock") "Nach Dateisperre geht es weiter."

    $console = Watch-VstLogFiles -Directory $logTestRoot -InitialTail 1 -SinglePass 6>&1 | Out-String
    Assert-LogTest ($console.Contains("[ShopService.out] after lock")) "Quellenangabe fuer Services."
    Assert-LogTest ($console.Contains("[PostgreSQL/postgresql.log] database ready")) "PostgreSQL-Ausgabe."
    Assert-LogTest ($console.Contains("[OpenTelemetryCollector.err] collector ready")) "Collector-Ausgabe."

    # Den echten Fenster-Startweg testen, nicht nur dot-sourcing von Funktionen:
    # -File ohne LogDirectory muss auch unter Windows PowerShell 5.1 funktionieren.
    $standaloneScript = Join-Path $logTestRoot "Watch-VSTLogs.ps1"
    Copy-Item -LiteralPath (Join-Path $logTestProjectRoot "Watch-VSTLogs.ps1") -Destination $standaloneScript
    $logTestFiles += $standaloneScript
    $powerShellExe = Join-Path $PSHOME "pwsh.exe"
    if (-not (Test-Path -LiteralPath $powerShellExe)) {
        $powerShellExe = Join-Path $PSHOME "powershell.exe"
    }
    $startupOutput = & $powerShellExe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
        -File $standaloneScript -Once -Tail 0 2>&1
    $startupExitCode = $LASTEXITCODE
    $startupText = $startupOutput | Out-String
    Assert-LogTest ($startupExitCode -eq 0) "Direkter -File-Aufruf: $startupText"
    Assert-LogTest ($startupText.Contains("Quelle: $(Join-Path $logTestRoot 'Logs')")) "Standardpfad wird relativ zum Subscript bestimmt."
    Write-Output "Live-Log-Tests erfolgreich: Tail, Pipeline, UTF-8, Teilwrites, Neustart, Dateisperren und Quellen."
}
finally {
    # Nur die explizit fuer diesen Test angelegten Dateien entfernen, nie rekursiv.
    foreach ($file in $logTestFiles) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
    foreach ($directory in @((Join-Path $logTestRoot "Startup"), (Join-Path $logTestRoot "PostgreSQL"), $logTestRoot)) {
        if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory }
    }
}
