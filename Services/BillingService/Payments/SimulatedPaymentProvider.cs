using VstOnlineStore.Observability;

namespace BillingService.Payments;

/// <summary>
/// Vorläufiger Adapter für den späteren Finanzdienstleister.
/// Er bestätigt gültige Demo-Zahlungen ohne ein externes System aufzurufen.
/// </summary>
public sealed class SimulatedPaymentProvider(
    IStructuredLogger logger) : TestPaymentProviderBase(logger) {

    public override string Key => "demo";

    public override string Name => "Holzwerk DemoPay";

    protected override string TransactionPrefix => "DEMO-";

    protected override string AcceptedMessage =>
        "Die Zahlung wurde vom Demo-Finanzdienstleister bestätigt.";

    protected override string DeclinedMessage =>
        "Der Demo-Finanzdienstleister hat die Zahlung abgelehnt.";
}
