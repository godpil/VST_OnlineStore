namespace BillingService.Payments;

/// <summary>
/// Einheitlicher, ausschließlich innerhalb der Payment-Fassade verwendeter
/// Vertrag für Zahlungsanbieter.
/// </summary>
public interface IPaymentProvider {
    string Key { get; }

    string Name { get; }

    bool IsTestMode { get; }

    Task<PaymentChargeResult> ChargeAsync(
        Guid orderId,
        long amountInCents,
        string currency,
        CancellationToken cancellationToken = default);

    Task<PaymentRefundResult> RefundAsync(
        string transactionId,
        long amountInCents,
        CancellationToken cancellationToken = default);

    Task<PaymentStatusResult> GetStatusAsync(
        string transactionId,
        CancellationToken cancellationToken = default);
}

public enum PaymentTransactionStatus {
    Unknown,
    Pending,
    Succeeded,
    Failed,
    PartiallyRefunded,
    Refunded
}

public sealed record PaymentProviderDescriptor(
    string Key,
    string Name,
    bool IsTestMode,
    bool IsActive,
    bool IsEnabled);

public sealed record PaymentChargeResult(
    bool Success,
    string TransactionId,
    PaymentTransactionStatus Status,
    string Message);

public sealed record PaymentRefundResult(
    bool Success,
    string TransactionId,
    long RefundedAmountInCents,
    long TotalRefundedAmountInCents,
    PaymentTransactionStatus Status,
    string Message);

public sealed record PaymentStatusResult(
    bool Found,
    string TransactionId,
    Guid OrderId,
    long AmountInCents,
    long RefundedAmountInCents,
    string Currency,
    PaymentTransactionStatus Status,
    string Message);
