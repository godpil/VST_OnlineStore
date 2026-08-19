using VstOnlineStore.Observability;

namespace BillingService.Payments;

/// <summary>
/// PayPal-Adapter für den lokalen Testbetrieb. Die öffentliche Provider-
/// Schnittstelle bleibt identisch zu einer späteren PayPal-Sandbox-Anbindung.
/// </summary>
public sealed class PayPalPaymentProvider(
    IStructuredLogger logger) : IPaymentProvider {

    public string Key => "paypal";

    public string Name => "PayPal";

    public bool IsTestMode => true;

    public Task<PaymentProviderResult> ChargeAsync(
        long amountInCents,
        string currency,
        string paymentMethod,
        string reference,
        CancellationToken cancellationToken = default) {

        cancellationToken.ThrowIfCancellationRequested();
        logger.Debug(
            "PayPal test payment started.",
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
            success ? $"PAYPAL-TEST-{Guid.NewGuid():N}" : string.Empty,
            success
                ? "Die Zahlung wurde vom PayPal-Testadapter bestätigt."
                : "Der PayPal-Testadapter hat die Zahlung abgelehnt.");

        PaymentLogContext.LogResult(logger, this, reference, amountInCents, currency, result);
        return Task.FromResult(result);
    }
}
