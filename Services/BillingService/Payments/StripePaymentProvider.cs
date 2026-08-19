using VstOnlineStore.Observability;

namespace BillingService.Payments;

/// <summary>
/// Stripe-Adapter für den lokalen Testbetrieb. Die öffentliche Provider-
/// Schnittstelle bleibt identisch zu einer späteren Stripe-Sandbox-Anbindung.
/// </summary>
public sealed class StripePaymentProvider(
    IStructuredLogger logger) : IPaymentProvider {

    public string Key => "stripe";

    public string Name => "Stripe";

    public bool IsTestMode => true;

    public Task<PaymentProviderResult> ChargeAsync(
        long amountInCents,
        string currency,
        string paymentMethod,
        string reference,
        CancellationToken cancellationToken = default) {

        cancellationToken.ThrowIfCancellationRequested();
        logger.Debug(
            "Stripe test payment started.",
            PaymentLogContext.Create(
                Key,
                Name,
                reference,
                amountInCents,
                currency,
                paymentMethod));

        var success = amountInCents > 0
            && currency.Equals("EUR", StringComparison.OrdinalIgnoreCase);
        var result = new PaymentProviderResult(
            success,
            success ? $"pi_test_{Guid.NewGuid():N}" : string.Empty,
            success
                ? "Die Zahlung wurde vom Stripe-Testadapter bestätigt."
                : "Der Stripe-Testadapter hat die Zahlung abgelehnt.");

        PaymentLogContext.LogResult(logger, this, reference, amountInCents, currency, result);
        return Task.FromResult(result);
    }
}
