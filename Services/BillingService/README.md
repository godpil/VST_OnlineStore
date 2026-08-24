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
- fachliche Operationen: `ProcessPayment`, `RefundPayment` und `GetPaymentStatus`
- Verwaltungsoperationen: `ListPaymentProviders` und `GetStatus` (Health-Check)
- Aufrufer: ShopService
- RabbitMQ-Exchange für Rechnungen: `vst.billing.events`
- Routing Key einer erfolgreichen Zahlung: `payment.succeeded`
- zusätzliche Audit-Ereignisse über RabbitMQ

Der Service veröffentlicht keine fachliche REST-API. Der Text-Endpunkt `/` ist
nur eine einfache Prozessdiagnose.

## Payment-Konfiguration

Unter `PaymentProviders` bestimmt `EnabledProviderKeys`, welche registrierten
Adapter für neue Zahlungen verwendet werden dürfen. `ActiveProviderKey` ist nur
der serverseitige Standard für interne oder ältere gRPC-Aufrufer ohne Schlüssel;
`TimeoutMilliseconds` begrenzt jede Provideroperation. Standardmäßig sind
`paypal` und `stripe` aktiviert, während `demo` deaktiviert bleibt. Der Timeout
beträgt 5000 Millisekunden.

Die .NET-Konfiguration kann wie gewohnt über eine Umgebungsvariable
überschrieben werden:

```powershell
$env:PaymentProviders__ActiveProviderKey = "paypal"
$env:PaymentProviders__EnabledProviderKeys__0 = "paypal"
$env:PaymentProviders__EnabledProviderKeys__1 = "stripe"
```

Die Änderung wird beim Start eingelesen und erfordert daher einen Neustart des
BillingService. `GET /api/payment-providers` zeigt über ShopService und
StoreProxy alle registrierten Adapter einschließlich ihres `isEnabled`-Status.
Der Warenkorb trifft keine Vorauswahl; `POST /api/orders` überträgt die bewusste
Auswahl des Kunden als `paymentProviderKey`.

Alle konkreten Implementierungen von `IPaymentProvider` werden beim Start
automatisch aus dem BillingService-Assembly registriert. Ein zusätzlicher
Adapter benötigt daher keine Änderung an `Program.cs`, der Fassade oder den
bestehenden Adaptern.

Die Fassade kapselt `ChargeAsync(providerKey, orderId, amount, currency)`,
`RefundAsync(transactionId, amount)` und `GetStatusAsync(transactionId)`. Sie
weist eine Belastung zurück, wenn der angeforderte Adapter nicht in
`EnabledProviderKeys` enthalten ist. Erstattung und Statusabfrage werden über
die gespeicherte Transaktionszuordnung weiterhin zum ursprünglichen Adapter
geroutet.
Nach einer erfolgreichen Belastung veröffentlicht der BillingService das
Ereignis `payment.succeeded`. Kann es nicht bestätigt an RabbitMQ übergeben
werden, meldet die Antwort `InvoiceQueued=false`; der ShopService stößt dann die
Erstattung und Freigabe der Reservierung als SAGA-Kompensation an.
Transaktionsstatus und Providerzuordnung werden im aktuellen lokalen
Testbetrieb im Speicher des BillingService gehalten und gehen bei dessen
Neustart verloren. Eine dauerhafte operative Transaktionspersistenz ist nicht
Teil der derzeitigen Testadapter; die unveränderlichen fachlichen
Audit-Snapshots werden davon unabhängig weiterhin in PostgreSQL gespeichert.

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

## Unit-Tests

```powershell
dotnet test .\Tests\BillingService.UnitTests\BillingService.UnitTests.csproj
```

Die Tests prüfen Erfolg, Ablehnung, einen Timeout für jeden der drei Anbieter,
die konfigurationsgesteuerte Providerfreigabe, die bestellungsbezogene Auswahl,
automatische Adaptererkennung, Erstattungen und transaktionsbezogene
Statusabfragen.
