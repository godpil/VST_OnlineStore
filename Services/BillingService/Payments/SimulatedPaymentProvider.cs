namespace BillingService.Payments;

/// <summary>
/// Vorläufiger Adapter für den späteren Finanzdienstleister.
/// Er bestätigt gültige Demo-Zahlungen ohne ein externes System aufzurufen.
/// </summary>
public sealed class SimulatedPaymentProvider : IPaymentProvider {
    public string Name => "Holzwerk DemoPay";

    public Task<PaymentProviderResult> ChargeAsync(
        long amountInCents,
        string currency,
        string paymentMethod,
        string reference,
        CancellationToken cancellationToken = default) {

        cancellationToken.ThrowIfCancellationRequested();

        var success = amountInCents > 0
            && currency.Equals("EUR", StringComparison.OrdinalIgnoreCase);

        return Task.FromResult(new PaymentProviderResult(
            success,
            success ? Guid.NewGuid().ToString("N") : string.Empty,
            success
                ? "Die Zahlung wurde vom Demo-Finanzdienstleister bestätigt."
                : "Der Demo-Finanzdienstleister hat die Zahlung abgelehnt."));
    }
}
