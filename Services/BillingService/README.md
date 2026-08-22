# BillingService

## Aufgabe

Der BillingService kapselt die Zahlungsabwicklung hinter einer gemeinsamen
Provider-Fassade. Registriert sind die Testadapter `demo`, `paypal` und `stripe`;
sie bewegen in der aktuellen Konfiguration kein echtes Geld. Nach einer
erfolgreichen Zahlung veröffentlicht der Service ein Ereignis zur asynchronen
Rechnungserstellung.

## Schnittstellen und Abhängigkeiten

- Adresse: `http://localhost:6684` über HTTP/2
- gRPC-Vertrag: `Contracts/billingservice.proto`
- Operationen: `ProcessPayment`, `ListPaymentProviders` und `GetStatus`
- Aufrufer: ShopService
- RabbitMQ-Exchange für Rechnungen: `vst.billing.events`
- Routing Key einer erfolgreichen Zahlung: `payment.succeeded`
- zusätzliche Audit-Ereignisse über RabbitMQ

Der Service veröffentlicht keine fachliche REST-API. Der Text-Endpunkt `/` ist
nur eine einfache Prozessdiagnose.

## Voraussetzungen

- .NET 10 SDK
- RabbitMQ auf `localhost:5672`
- freier TCP-Port `6684`
- optional der vom Betriebsskript verwaltete OpenTelemetry Collector

Für echte PayPal- oder Stripe-Zahlungen wären zusätzliche Zugangsdaten,
Webhooks und eine gesonderte Produktionsfreigabe erforderlich. Diese sind für
den aktuellen Testbetrieb ausdrücklich nicht notwendig.

## Start

Bevorzugter Einzelstart vom Repository-Wurzelverzeichnis:

```powershell
.\Start-VSTOnlineStore.ps1 -Action StartService -ServiceName BillingService
```

Direkter Start über das .NET SDK:

```powershell
dotnet restore .\Services\BillingService\BillingService.csproj
dotnet run --project .\Services\BillingService\BillingService.csproj --launch-profile BillingService
```

Damit Rechnungen erzeugt werden, müssen zusätzlich InvoiceService und RabbitMQ
laufen. Der BillingService selbst kann unabhängig vom InvoiceService gestartet
werden; die Kopplung erfolgt über den Broker.

```powershell
.\Start-VSTOnlineStore.ps1 -Action StopService -ServiceName BillingService
```
