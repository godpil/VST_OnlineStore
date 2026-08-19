# Dateiklassifikation der VSTOnlineStore-Solution

Stand: 20. August 2026

Diese Liste bewertet die Dateien nach ihrer Bedeutung für den **aktuellen
Produkt-Build und den lokalen Standardbetrieb**. "Obligatorisch" bedeutet daher
nicht, dass .NET eine Datei grundsätzlich verlangt, sondern dass der derzeitige
Funktionsumfang ohne sie nicht unverändert gebaut oder betrieben werden kann.

## Legende

| Kürzel | Bedeutung |
|---|---|
| **O** | Obligatorisch für den aktuellen Produkt-Build oder Standardbetrieb |
| **B** | Bedingt obligatorisch: nur für die genannte Betriebsart oder Funktion |
| **F** | Fakultativ/optional: nützlich, aber ohne Einfluss auf die Produktlaufzeit |
| **E** | Entbehrlich, veraltet oder vollständig regenerierbar |

## 1. Aktive Produkt-Solution

### Solution, Betrieb und Repository

| Klasse | Datei | Begründung |
|---|---|---|
| **O** | `VST_OnlineStore.slnx` | Wird vom Startskript restauriert und gebaut. Bei manuellem Einzelprojekt-Build technisch ersetzbar. |
| **O** | `Start-VSTOnlineStore.ps1` | Vorgesehener lokaler Start-, Stop-, Status- und Diagnoseweg für den gesamten Stack. Bei vollständig manuellem Start technisch ersetzbar. |
| **F** | `README.md` | Betriebs- und Architekturdokumentation; keine Build-Abhängigkeit. |
| **F** | `.gitignore` | Keine Runtime-Abhängigkeit, aber wichtig für saubere Versionsverwaltung. |
| **F** | `.gitattributes` | Keine Runtime-Abhängigkeit; normalisiert unter anderem Zeilenenden in Git. |
| **E** | `.dockerignore` | Es gibt weder Dockerfile noch Compose-Datei; der Stack wird ausdrücklich ohne Docker betrieben. |

### Gemeinsame gRPC-Verträge

Alle fünf Dateien unter `Contracts/` sind **O**, weil mindestens ein aktives
Client- und Serverprojekt den jeweiligen Vertrag kompiliert.

| Datei | Beziehung |
|---|---|
| `Contracts/warehouseservice.proto` | ShopService -> WarehouseService |
| `Contracts/billingservice.proto` | ShopService -> BillingService |
| `Contracts/invoiceservice.proto` | ShopService -> InvoiceService |
| `Contracts/auditservice.proto` | ShopService -> AuditService; ausschließlich Lese-/Statuszugriff |
| `Contracts/storebackend.proto` | WarehouseService -> StoreBackend |

### StoreProxy

| Klasse | Dateien | Begründung |
|---|---|---|
| **O** | `StoreProxy/StoreProxy.csproj`, `StoreProxy/Program.cs`, `StoreProxy/YarpErrorHandlingMiddleware.cs` | Projektdefinition, YARP-Gateway und Fehlerbehandlung. |
| **O** | `StoreProxy/appsettings.json` | Enthält die veröffentlichten YARP-Routen, das einzige Ziel ShopService, Timeouts, Rate Limits und Health Checks. |
| **O** | `StoreProxy/wwwroot/index.html` | Einstiegspunkt der produktiven Website. |
| **O** | `StoreProxy/wwwroot/css/store.css` | Produktives Seitenlayout. |
| **O** | `StoreProxy/wwwroot/js/StoreAPI.js` | Gemeinsamer Browser-API-Adapter mit relativen Proxy-URLs und Correlation-ID-Behandlung. |
| **O** | `StoreProxy/wwwroot/js/productCard.js` | Rendert Produktkarten; wird von `WebstoreApp.js` verwendet. |
| **O** | `StoreProxy/wwwroot/js/WebstoreApp.js` | UI-, Warenkorb- und Checkout-Steuerung. |
| **F** | `StoreProxy/wwwroot/images/wood-background.png` | Rein kosmetische, in CSS referenzierte Grafik; die Anwendung funktioniert ohne sie. |
| **F** | `StoreProxy/appsettings.Development.json` | Entwicklungs-Override für Log-Level; die Basiskonfiguration genügt. |
| **F** | `StoreProxy/Properties/launchSettings.json` | Nur für Visual Studio/F5 oder `dotnet run`; das Startskript setzt URL und Umgebung selbst. |

### ShopService

| Klasse | Dateien | Begründung |
|---|---|---|
| **O** | `Services/ShopService/ShopService.csproj`, `Program.cs`, `appsettings.json` | Projekt, REST-Grenze/Composition Root und zwingende Downstream-Adressen. |
| **O** | `Checkout/CheckoutModels.cs`, `Checkout/CheckoutOrchestrator.cs`, `Checkout/AuditSnapshotRecorder.cs` | Checkout-Verträge, fachliche Orchestrierung, Kompensation und asynchrones Audit. |
| **O** | `Orchestration/ServiceStatusOrchestrator.cs` | Aggregierter Servicestatus und Readiness-Prüfung des Startskripts. |
| **O** | `Queries/AuditQueryEndpoints.cs`, `Queries/InvoiceQueryEndpoints.cs` | Einzig erlaubte synchrone Lesewege zu AuditService und InvoiceService. |
| **F** | `Properties/launchSettings.json` | Nur IDE-/`dotnet run`-Startprofil. |

### WarehouseService

| Klasse | Dateien | Begründung |
|---|---|---|
| **O** | `Services/WarehouseService/WarehouseService.csproj`, `Program.cs`, `appsettings.json` | Projekt, gRPC-Host und zwingende StoreBackend-Adresse/HTTP2-Konfiguration. |
| **O** | `GrpcServices/WarehouseCatalogGrpcService.cs` | Shop-facing Lagergrenze und alleiniger Aufrufer des StoreBackend. |
| **F** | `Properties/launchSettings.json` | Nur IDE-/`dotnet run`-Startprofil. |

### BillingService

| Klasse | Dateien | Begründung |
|---|---|---|
| **O** | `Services/BillingService/BillingService.csproj`, `Program.cs`, `appsettings.json` | Projekt, Host/DI sowie gRPC- und RabbitMQ-Konfiguration. |
| **O** | `GrpcServices/BillingOperationsGrpcService.cs` | Providerliste und Zahlungsoperationen. |
| **O** | `Messaging/IPaymentSucceededEventPublisher.cs`, `Messaging/RabbitMqPaymentSucceededEventPublisher.cs` | Ausschließlich asynchroner BillingService-zu-InvoiceService-Weg. |
| **O** | `Payments/IPaymentProvider.cs`, `Payments/PaymentProviderResolver.cs`, `Payments/PaymentLogContext.cs` | Adaptervertrag, Auswahl und gemeinsamer Log-Kontext. |
| **O** | `Payments/SimulatedPaymentProvider.cs`, `Payments/PayPalPaymentProvider.cs`, `Payments/StripePaymentProvider.cs` | Die drei aktuell zugesagten und registrierten Zahlungsadapter. |
| **F** | `Properties/launchSettings.json` | Nur IDE-/`dotnet run`-Startprofil. |

### InvoiceService

| Klasse | Dateien | Begründung |
|---|---|---|
| **O** | `Services/InvoiceService/InvoiceService.csproj`, `Program.cs`, `appsettings.json` | Projekt, Host/DI sowie QuestPDF-, RabbitMQ-, HTTP2- und E-Mail-Konfiguration. |
| **O** | `Application/InvoiceApplicationService.cs` | Idempotente Ereignisverarbeitung, PDF-Erzeugung, Persistenz, Versand und Audit. |
| **O** | `Application/Ports/IInvoiceEmailSender.cs`, `IInvoicePdfRenderer.cs`, `IInvoiceRepository.cs` | Adapterports der Anwendungsschicht. |
| **O** | `Domain/InvoiceRecord.cs` | Persistiertes Rechnungsmodell. |
| **O** | `Messaging/RabbitMqPaymentSucceededEventConsumer.cs` | Vorgesehener asynchroner Eingang vom BillingService. |
| **O** | `Pdf/QuestPdfInvoiceRenderer.cs` | Geforderte PDF-Erzeugung mit QuestPDF. |
| **O** | `Storage/JsonInvoiceRepository.cs` | Aktueller Datenbankadapter auf JSON-Basis. |
| **O** | `GrpcServices/InvoiceOperationsGrpcService.cs` | Read-only PDF-/Statusschnittstelle für den ShopService. |
| **O** | `Email/InvoiceEmailOptions.cs`, `PickupDirectoryInvoiceEmailSender.cs`, `SmtpInvoiceEmailSender.cs` | Konfiguration und beide im Composition Root referenzierten E-Mail-Adapter. |
| **F** | `Properties/launchSettings.json` | Nur IDE-/`dotnet run`-Startprofil. |
| **B** | `Data/invoices.json` | Persistierter Laufzeitzustand. Wird bei Fehlen leer angelegt; Entfernen löscht jedoch gespeicherte Rechnungen. |
| **F** | `Data/email-outbox/.gitkeep` | Nur Git-Platzhalter; der Pickup-Adapter erzeugt das Verzeichnis selbst. |

### AuditService

| Klasse | Dateien | Begründung |
|---|---|---|
| **O** | `Services/AuditService/AuditService.csproj`, `Program.cs`, `appsettings.json` | Projekt, Host/DI sowie gRPC-, RabbitMQ- und Datenpfad-Konfiguration. |
| **O** | `Domain/AuditSnapshot.cs` | Snapshot-, Draft- und Enum-Domänenvertrag. |
| **O** | `Application/Ports/IAuditSnapshotRepository.cs`, `Application/AuditApplicationService.cs` | Persistenzport, Validierung sowie Schreiben und chronologische Abfrage. |
| **O** | `Storage/JsonAuditSnapshotRepository.cs` | Append-only JSON-Datenbankadapter mit Verkettung und Idempotenz. |
| **O** | `Messaging/RabbitMqAuditEventConsumer.cs` | Einziger Audit-Schreibweg, einschließlich Dead-Letter-Queue. |
| **O** | `GrpcServices/AuditOperationsGrpcService.cs` | Read-only Snapshot-/Statusschnittstelle für den ShopService. |
| **F** | `Properties/launchSettings.json` | Nur IDE-/`dotnet run`-Startprofil. |
| **B** | `Data/audit-snapshots.json` | Persistierte Audit-Historie. Wird bei Fehlen leer angelegt; Entfernen vernichtet jedoch die vorhandene Ereigniskette. |

### StoreBackend

| Klasse | Dateien | Begründung |
|---|---|---|
| **O** | `StoreBackend/StoreBackend.csproj`, `Program.cs`, `appsettings.json` | Projekt, interner gRPC-Persistenzadapter und HTTP2-/Datenpfad-Konfiguration. |
| **O** | `Application/Ports/IWarehouseRepository.cs`, `Application/WarehouseApplicationService.cs` | Speicherport, Bestandslogik, Reservieren und Freigeben. |
| **O** | `Services/WarehouseStorageGrpcService.cs` | Transportadapter für den WarehouseService. |
| **O** | `Storage/JsonWarehouseRepository.cs` | Aktueller JSON-Persistenzadapter und Startbestands-Fallback. |
| **O** | `Domain/WarehouseProduct.cs`, `WarehouseOrderItem.cs`, `ProductStock.cs`, `StockChangeResult.cs` | Im aktuellen Bestandsfluss verwendete Domänenmodelle. |
| **F** | `Properties/launchSettings.json` | Nur IDE-/`dotnet run`-Startprofil. |
| **B** | `Data/warehouse-products.json` | Persistierter Lagerbestand. Wird bei Fehlen mit Startdaten erzeugt; Entfernen setzt den Bestand zurück. |

### Gemeinsame Observability-Bibliothek

Das Projekt `Shared/VstOnlineStore.Observability` wird von allen sieben
Laufzeitprojekten referenziert. Für den unveränderten Build sind daher die
Projektdatei und alle folgenden Quelldateien **O**:

- Projekt: `VstOnlineStore.Observability.csproj`
- Correlation-ID: `CorrelationId.cs`, `CorrelationIdMiddleware.cs`,
  `CorrelationIdExtensions.cs`, `CorrelationIdDelegatingHandler.cs`
- strukturierte Logs und OTLP: `IStructuredLogger.cs`, `StructuredLogger.cs`,
  `StructuredLogEntry.cs`, `StructuredLogLevel.cs`,
  `StructuredLoggingOptions.cs`, `DailyJsonLogFileSink.cs`,
  `StructuredRequestLoggingMiddleware.cs`, `OpenTelemetryExtensions.cs`
- asynchrones Audit: `Auditing/AuditEventEnvelope.cs`,
  `Auditing/IAuditEventPublisher.cs`,
  `Auditing/RabbitMqAuditEventPublisher.cs`,
  `Auditing/RabbitMqAuditExtensions.cs`,
  `Auditing/RabbitMqAuditOptions.cs`
- Billing-zu-Invoice-Ereignis: `Messaging/PaymentSucceededEvent.cs`,
  `Messaging/RabbitMqInvoiceOptions.cs`

## 2. Bedingte Betriebsdateien und Laufzeitzustand

| Klasse | Datei/Gruppe | Bedeutung |
|---|---|---|
| **B** | `Observability/otel-collector-config.yaml` | Obligatorisch, wenn der native OpenTelemetry Collector verwendet wird; mit `-SkipCollector` für die Fachfunktion entbehrlich. |
| **B** | `Tools/OpenTelemetryCollector/<Version>/otelcol-contrib.exe` | Lokale Collector-Binärdatei; wird geprüft heruntergeladen und ist daher nicht zu versionieren. |
| **B** | RabbitMQ Serverinstallation | Kein Repositorybestandteil, aber für Billing-zu-Invoice und Audit-Snapshots zur Laufzeit erforderlich. |
| **B** | `Logs/<Service>/<Service>-<UTC-Datum>.jsonl` | Täglich rollierende strukturierte Service-Logs; erzeugt, 14-Tage-Fenster. |
| **B** | `Logs/OpenTelemetry/vst-online-store.jsonl` | Zentrale technische OTLP-Senke; nur bei aktivem Collector. |
| **B** | `Logs/Processes/vst-online-store-processes.json` | Vom Startskript verwaltetes Prozessmanifest. |
| **B** | `Logs/Startup/*.stdout.log`, `Logs/Startup/*.stderr.log` | Prozessausgaben zur lokalen Diagnose. |
| **B** | `Services/InvoiceService/Data/email-outbox/*.eml` | Pickup-E-Mail-Senke im aktuellen lokalen E-Mail-Modus; kann personenbezogene Daten enthalten. |
| **E** | `.vs/`, `**/bin/`, `**/obj/`, `TestResults/` | IDE-, Build- und Testergebnisse; jederzeit regenerierbar. |

Die drei JSON-Datendateien sind technisch bootstrappbar, aber **nicht beliebig
löschbar**, sobald ihr Zustand erhalten werden soll. Insbesondere Audit-Payloads,
Kunden-E-Mail-Adressen, Rechnungs-PDFs und EML-Dateien gehören nicht dauerhaft
in die Versionsverwaltung.

## 3. Fakultative Tutorials, Werkzeuge und Dokumentation

### In der Solution enthalten, aber nicht Teil der Produktlaufzeit

| Klasse | Dateien | Einordnung |
|---|---|---|
| **F/B** | `PilService/PilService.csproj`, `Program.cs`, `Services/PilServiceImplementation.cs`, `Protos/pilservice.proto`, `appsettings.json`, `appsettings.Development.json`, `Properties/launchSettings.json` | Historisches gRPC-Tutorial. Fachlich optional, derzeit aber für einen unveränderten Build der gesamten `.slnx` nötig, weil das Projekt dort eingetragen ist. |
| **F/B** | `GRPCTester/GRPCTester.csproj`, `Program.cs` | Historischer Tutorial-Client. Fachlich optional, aber derzeit Solution-Build-Mitglied. Enthält einen nicht portablen absoluten Proto-Pfad und passt beim Port nicht zum PilService. |
| **F/B** | `Tools/Tools.csproj`, `Tools/ConsoleTools.cs` | Nicht referenziertes Hilfsprojekt. Fachlich optional, aber derzeit Solution-Build-Mitglied. |

Diese Projekte können in eine separate Tutorial-Solution verschoben oder aus
`VST_OnlineStore.slnx` entfernt werden. Erst dann sind ihre Dateien auch für den
vollständigen Solution-Build entbehrlich.

### Dokumentationsartefakte

| Klasse | Datei | Einordnung |
|---|---|---|
| **F** | `Dokumentation/HappyPath.drawio` | Bearbeitbares Architekturdiagramm. |
| **F** | `Dokumentation/Requirements.txt` | Betriebs-/Anforderungsnotizen. |
| **F** | `Dokumentation/VST_Onlinestore_Vorpraesentation.pptx` | Präsentationsquelle. |
| **F** | `Dokumentation/VST_Onlinestore_PDF_Vorpraesentation.pdf` | Exportierte Präsentation. |
| **F** | `Dokumentation/VSTOnlineStore-Service-Abhaengigkeiten-korrigiert-4K.png` | Korrigierte, gerenderte Architekturübersicht. |
| **F** | `output/pdf/vst-sample-invoice.pdf` | Nicht referenzierte Musterrechnung. |
| **E** | `Dokumentation/VSTOnlineStore-Service-Abhaengigkeiten-3840x1080.png` | Bereits verworfene Diagrammversion. |
| **E** | `Dokumentation/.$HappyPath.drawio.bkp` | Automatische Draw.io-Sicherungsdatei. |

## 4. Veraltete oder unbenutzte Dateien

| Klasse | Datei/Gruppe | Befund |
|---|---|---|
| **E** | `store.css`, `StoreAPI.js`, `WebstoreApp.js`, `WebstoreMainsite.html` im Repository-Root | Veraltete Vorgänger der produktiven Dateien unter `StoreProxy/wwwroot`; werden nicht ausgeliefert. `WebstoreMainsite.html` referenziert zudem eine nicht vorhandene `app.js`. |
| **E** | Virtuelle Solution-Einträge unter `/Frontend/Website` | Zeigen teilweise auf die Root-Duplikate und teilweise auf nicht vorhandene Dateien (`index.html`, `productCard.js`); das aktive Frontend liegt in `StoreProxy/wwwroot`. |
| **E** | `erl_crash.dump` | Versioniertes Erlang/RabbitMQ-Crash-Artefakt; keine Projekt- oder Laufzeitabhängigkeit. |
| **E** | leeres `Data/` im Repository-Root | Unbenutzt und nicht referenziert. |
| **E** | ehemaliger Ordner `Webfrontend/` | Unvollständiger React-Prototyp ohne `package.json`, Build-Einstieg oder Referenz. Er wurde nach Referenzprüfung entfernt. |

## 5. Empfohlene nächste Bereinigung

Ohne Einfluss auf die aktuelle Produktlaufzeit können in einem separaten,
leicht prüfbaren Änderungsschritt entfernt werden:

1. `.dockerignore`
2. `erl_crash.dump` und eine passende Ignore-Regel
3. `Dokumentation/.$HappyPath.drawio.bkp` und `*.drawio.bkp` in `.gitignore`
4. die verworfene 3840x1080-PNG
5. die vier Root-Legacy-Frontenddateien und die toten Frontend-Einträge aus
   `VST_OnlineStore.slnx`
6. das nicht verwendete `Tools`-Projekt
7. optional PilService und GRPCTester, wenn die Tutorials nicht mehr benötigt
   werden
8. die veränderlichen JSON-/EML-Datensenken aus der Versionsverwaltung lösen
   und nur neutrale Beispieldaten separat vorhalten

Diese Kandidaten sind in dieser Bestandsaufnahme bewusst **nicht automatisch
gelöscht** worden.
