namespace BillingService.Payments;

public interface IPaymentProvider {
    string Key { get; }

    string Name { get; }

    bool IsTestMode { get; }

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
