using VstOnlineStore.Observability;

namespace BillingService.Payments;

internal static class PaymentLogContext {
    public static object CreateCharge(
        string providerKey,
        string providerName,
        Guid orderId,
        long amountInCents,
        string currency) => new {
            providerKey,
            providerName,
            orderId,
            amountInCents,
            currency,
            testMode = true
        };

    public static void LogChargeResult(
        IStructuredLogger logger,
        IPaymentProvider provider,
        Guid orderId,
        long amountInCents,
        string currency,
        PaymentChargeResult result) {

        var context = new {
            providerKey = provider.Key,
            providerName = provider.Name,
            orderId,
            amountInCents,
            currency,
            result.Success,
            transactionId = EmptyAsNull(result.TransactionId),
            status = result.Status.ToString(),
            provider.IsTestMode
        };

        if (result.Success) {
            logger.Info("Payment provider accepted the payment.", context);
        }
        else {
            logger.Warn("Payment provider rejected the payment.", context);
        }
    }

    public static void LogRefundResult(
        IStructuredLogger logger,
        IPaymentProvider provider,
        PaymentRefundResult result) {

        var context = new {
            providerKey = provider.Key,
            providerName = provider.Name,
            result.TransactionId,
            result.Success,
            result.RefundedAmountInCents,
            result.TotalRefundedAmountInCents,
            status = result.Status.ToString(),
            provider.IsTestMode
        };

        if (result.Success) {
            logger.Info("Payment provider refunded the payment.", context);
        }
        else {
            logger.Warn("Payment provider rejected the refund.", context);
        }
    }

    private static string? EmptyAsNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
