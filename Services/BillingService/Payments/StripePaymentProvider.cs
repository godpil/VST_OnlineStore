using VstOnlineStore.Observability;

namespace BillingService.Payments;

/// <summary>
/// Stripe-Adapter für den lokalen Testbetrieb. Die öffentliche Provider-
/// Schnittstelle bleibt identisch zu einer späteren Stripe-Sandbox-Anbindung.
/// </summary>
public sealed class StripePaymentProvider(
    IStructuredLogger logger) : TestPaymentProviderBase(logger) {

    public override string Key => "stripe";

    public override string Name => "Stripe";

    protected override string TransactionPrefix => "pi_test_";

    protected override string AcceptedMessage =>
        "Die Zahlung wurde vom Stripe-Testadapter bestätigt.";

    protected override string DeclinedMessage =>
        "Der Stripe-Testadapter hat die Zahlung abgelehnt.";
}
