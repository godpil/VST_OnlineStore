using VstOnlineStore.Observability;

namespace BillingService.Payments;

/// <summary>
/// PayPal-Adapter für den lokalen Testbetrieb. Die öffentliche Provider-
/// Schnittstelle bleibt identisch zu einer späteren PayPal-Sandbox-Anbindung.
/// </summary>
public sealed class PayPalPaymentProvider(
    IStructuredLogger logger) : TestPaymentProviderBase(logger) {

    public override string Key => "paypal";

    public override string Name => "PayPal";

    protected override string TransactionPrefix => "PAYPAL-TEST-";

    protected override string AcceptedMessage =>
        "Die Zahlung wurde vom PayPal-Testadapter bestätigt.";

    protected override string DeclinedMessage =>
        "Der PayPal-Testadapter hat die Zahlung abgelehnt.";
}
