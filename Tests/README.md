# Automatisierte Tests

Die Tests sind bewusst vom operativen Code getrennt:

- `BillingService.UnitTests` prüft die Payment-Fassade einschließlich Erfolg,
  Ablehnung, Timeout für DemoPay, PayPal und Stripe sowie Anbieterwechsel über
  die Konfiguration. Zusätzlich werden automatische Adaptererkennung,
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
  DemoPay-Adapters vor der Zahlung.

Alle Tests vom Repository-Wurzelverzeichnis ausführen:

```powershell
dotnet test .\VST_OnlineStore.slnx
```

Die Testprojekte lassen sich auch einzeln ausführen:

```powershell
dotnet test .\Tests\BillingService.UnitTests\BillingService.UnitTests.csproj
dotnet test .\Tests\ShopService.IntegrationTests\ShopService.IntegrationTests.csproj
```
