namespace BillingService.Payments;

public interface IPaymentProvider {
    string Name { get; }

    Task<PaymentProviderResult> ChargeAsync(
        long amountInCents,
        string currency,
        string paymentMethod,
        string reference,
        CancellationToken cancellationToken = default);
}

public sealed record PaymentProviderResult(
    bool Success,
    string TransactionId,
    string Message);
