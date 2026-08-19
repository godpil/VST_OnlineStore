using VstOnlineStore.Observability;

namespace BillingService.Payments;

internal static class PaymentLogContext {
    public static object Create(
        string providerKey,
        string providerName,
        string reference,
        long amountInCents,
        string currency,
        string paymentMethod) => new {
            providerKey,
            providerName,
            reference,
            amountInCents,
            currency,
            paymentMethodSupplied = !string.IsNullOrWhiteSpace(paymentMethod),
            testMode = true
        };

    public static void LogResult(
        IStructuredLogger logger,
        IPaymentProvider provider,
        string reference,
        long amountInCents,
        string currency,
        PaymentProviderResult result) {

        var context = new {
            providerKey = provider.Key,
            providerName = provider.Name,
            reference,
            amountInCents,
            currency,
            result.Success,
            transactionId = EmptyAsNull(result.TransactionId),
            provider.IsTestMode
        };

        if (result.Success) {
            logger.Info("Payment provider accepted the payment.", context);
        }
        else {
            logger.Warn("Payment provider rejected the payment.", context);
        }
    }

    private static string? EmptyAsNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
