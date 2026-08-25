# InvoiceService

## Aufgabe

Der InvoiceService verarbeitet erfolgreiche Zahlungen asynchron, erzeugt mit
QuestPDF eine PDF-Rechnung, persistiert sie und stellt sie dem ShopService per
gRPC bereit. Zusätzlich versendet er die Rechnung über eine konfigurierbare
E-Mail-Senke.

## Schnittstellen, Messaging und Daten

- Adresse: `http://localhost:6685` über HTTP/2
- gRPC-Vertrag: `Contracts/invoiceservice.proto`
- Operationen: `GetInvoicePdf` und `GetStatus`
- Aufrufer: ShopService
- RabbitMQ-Queue: `vst.invoice.payment-succeeded`
- Persistenz: `Services/InvoiceService/Data/invoices.json`
- Standard-E-Mail-Modus: Pickup-Verzeichnis
  `Services/InvoiceService/Data/email-outbox`

Im Standardmodus `Pickup` entsteht pro Nachricht eine lokale MIME-Datei mit
angehängter PDF-Rechnung; ein SMTP-Server ist dann nicht erforderlich. Für den
Modus `Smtp` müssen unter `InvoiceEmail:Smtp` Host, Port und Zugangsdaten
konfiguriert werden. MimeKit erstellt die Nachricht in beiden Modi;
MailKit übernimmt im optionalen SMTP-Modus die verschlüsselte Übertragung.

Der erfolgreiche Checkout wartet nur darauf, dass der BillingService das
Ereignis `payment.succeeded` bestätigt veröffentlicht. PDF-Erzeugung,
Persistierung und E-Mail-Ausgabe erfolgen danach asynchron. Ein früher Abruf der
Rechnungs-URL kann deshalb noch `404 Not Found` liefern; derselbe Abruf ist nach
abgeschlossener Verarbeitung erfolgreich. Wiederholte Zustellungen desselben
Events werden über die Event- und Invoice-ID idempotent behandelt.

Fehler während der Nachrichtenverarbeitung werden gemäß `InvoiceRetry` mit
mindestens drei Versuchen behandelt. Jeder fehlgeschlagene Versuch erzeugt
einen Snapshot `INVOICE_RETRY_SCHEDULED` beziehungsweise abschließend
`INVOICE_RETRY_EXHAUSTED`; danach folgt `INVOICE_PROCESSING_FAILED` und die
Nachricht gelangt in die Dead-Letter-Queue. Weil die Zahlung vor diesem
asynchronen Schritt bereits erfolgreich und das Ereignis dauerhaft publiziert
wurde, wird sie dabei nicht erstattet. Das Vorführszenario
`invoice-service-unavailable` löst genau diesen Pfad deterministisch aus.

## Voraussetzungen

- .NET 10 SDK
- RabbitMQ auf `localhost:5672`
- freier TCP-Port `6685`
- Schreibzugriff auf das Verzeichnis `Services/InvoiceService/Data`
- optional ein SMTP-Server, falls `InvoiceEmail:Mode` auf `Smtp` gesetzt wird
- optional der vom Betriebsskript verwaltete OpenTelemetry Collector

## Start

Bevorzugter Einzelstart vom Repository-Wurzelverzeichnis:

```powershell
.\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName InvoiceService
```

Direkter Start über das .NET SDK:

```powershell
dotnet restore .\Services\InvoiceService\InvoiceService.csproj
dotnet run --project .\Services\InvoiceService\InvoiceService.csproj --launch-profile InvoiceService
```

Der BillingService muss nicht vorher gestartet werden. Sobald er erfolgreiche
Zahlungen an RabbitMQ sendet, verarbeitet der laufende InvoiceService die
Ereignisse.

```powershell
.\Start-VSTOnlineStore.ps1 -Action StopService -ServiceName InvoiceService
```
