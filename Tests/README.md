# Automatisierte Tests

Die Tests sind bewusst vom operativen Code getrennt:

- `BillingService.UnitTests` prüft die Payment-Fassade einschließlich Erfolg,
  Ablehnung und Timeout für DemoPay, PayPal und Stripe. Zusätzlich werden
  automatische Adaptererkennung,
  konfigurationsgesteuerte Deaktivierung, bestellungsbezogene Auswahl,
  Erstattung und transaktionsbezogene Statusabfrage geprüft.
- `ShopService.IntegrationTests` ruft die öffentliche Bestellressource auf und
  prüft den Happy Path sowie zwei erwartete Fehlerszenarien (`fail1` und
  `fail2`). Die Fehlerszenarien erwarten fachliche Fehlerantworten; die
  Testsuite selbst bleibt bei korrektem Verhalten erfolgreich. Der Happy Path
  erwartet `201 Created` und verifiziert Reservierung, Zahlung, genau ein
  eingeplantes Rechnungsevent und den Lager-Commit. Zusätzlich muss
  `STOCK_COMMITTED` vor `ORDER_COMPLETED` im Audit stehen. Weitere Tests prüfen
  die Propagation einer Stripe-Auswahl und die Ablehnung des deaktivierten
  DemoPay-Adapters vor der Zahlung. Die SAGA-Tests verifizieren außerdem die
  terminalen Statuswerte `OUT_OF_STOCK` und `PAYMENT_FAILED` sowie Refund,
  Reservierungsfreigabe und die einzelnen Kompensations-Snapshots bei einer
  fehlgeschlagenen Warehouse-Ausbuchung (`ROLLBACK_COMPLETED`).

Alle Tests vom Repository-Wurzelverzeichnis ausführen:

```powershell
dotnet test .\VST_OnlineStore.slnx
```

Die Testprojekte lassen sich auch einzeln ausführen:

```powershell
dotnet test .\Tests\BillingService.UnitTests\BillingService.UnitTests.csproj
dotnet test .\Tests\ShopService.IntegrationTests\ShopService.IntegrationTests.csproj
```

Die Live-Logansicht hat zusätzlich einen eigenständigen PowerShell-Test ohne
weitere Pakete. Er verwendet ausschließlich temporäre Dateien und startet
oder stoppt keine Services:

```powershell
.\Tests\Scripts\Watch-VSTLogs.Tests.ps1
.\Tests\Scripts\Start-VSTLogWindow.Tests.ps1
```

Geprüft werden Quellenfilter, anfänglicher Rückblick, neue Meldungen, geteilte
UTF-8-Zeichen und Zeilen, zurückgesetzte Logdateien, kurzzeitige Dateisperren
und die Quellenangaben in der Konsolenausgabe.
Ein separater PowerShell-Prozess prüft außerdem den direkten `-File`-Start
ohne expliziten Logpfad, wie er beim Öffnen des Logfensters verwendet wird.
Die Start-/Stop-Tests ersetzen alle Prozessaktionen durch Testfunktionen und
prüfen das automatische Öffnen, `-NoLogWindow`, Fehlerisolation und Aufräumen.
