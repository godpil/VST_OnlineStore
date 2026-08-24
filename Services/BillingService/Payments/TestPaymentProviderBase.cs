using System.Collections.Concurrent;
using VstOnlineStore.Observability;

namespace BillingService.Payments;

/// <summary>
/// Gemeinsames Verhalten der lokalen Testadapter. Echte Provideradapter können
/// IPaymentProvider direkt implementieren und ihre jeweilige API verwenden.
/// </summary>
public abstract class TestPaymentProviderBase(
    IStructuredLogger logger) : IPaymentProvider {

    private readonly ConcurrentDictionary<string, TransactionState> _transactions =
        new(StringComparer.OrdinalIgnoreCase);

    public abstract string Key { get; }

    public abstract string Name { get; }

    public bool IsTestMode => true;

    protected abstract string TransactionPrefix { get; }

    protected abstract string AcceptedMessage { get; }

    protected abstract string DeclinedMessage { get; }

    public Task<PaymentChargeResult> ChargeAsync(
        Guid orderId,
        long amountInCents,
        string currency,
        CancellationToken cancellationToken = default) {

        cancellationToken.ThrowIfCancellationRequested();
        logger.Debug(
            "Payment provider charge started.",
            PaymentLogContext.CreateCharge(
                Key,
                Name,
                orderId,
                amountInCents,
                currency));

        var success = orderId != Guid.Empty
            && amountInCents > 0
            && currency.Equals("EUR", StringComparison.OrdinalIgnoreCase);
        var transactionId = success
            ? $"{TransactionPrefix}{Guid.NewGuid():N}"
            : string.Empty;
        if (success) {
            _transactions[transactionId] = new TransactionState(
                orderId,
                amountInCents,
                currency.ToUpperInvariant());
        }

        var result = new PaymentChargeResult(
            success,
            transactionId,
            success
                ? PaymentTransactionStatus.Succeeded
                : PaymentTransactionStatus.Failed,
            success ? AcceptedMessage : DeclinedMessage);

        PaymentLogContext.LogChargeResult(
            logger,
            this,
            orderId,
            amountInCents,
            currency,
            result);
        return Task.FromResult(result);
    }

    public Task<PaymentRefundResult> RefundAsync(
        string transactionId,
        long amountInCents,
        CancellationToken cancellationToken = default) {

        cancellationToken.ThrowIfCancellationRequested();
        if (!_transactions.TryGetValue(transactionId, out var transaction)) {
            return Task.FromResult(new PaymentRefundResult(
                false,
                transactionId,
                0,
                0,
                PaymentTransactionStatus.Unknown,
                "Die Zahlungstransaktion wurde beim Anbieter nicht gefunden."));
        }

        PaymentRefundResult result;
        lock (transaction.SyncRoot) {
            var remainingAmount = transaction.AmountInCents - transaction.RefundedAmountInCents;
            if (amountInCents <= 0 || amountInCents > remainingAmount) {
                result = new PaymentRefundResult(
                    false,
                    transactionId,
                    0,
                    transaction.RefundedAmountInCents,
                    transaction.Status,
                    "Der Erstattungsbetrag überschreitet den noch erstattbaren Betrag.");
            }
            else {
                transaction.RefundedAmountInCents += amountInCents;
                transaction.Status = transaction.RefundedAmountInCents == transaction.AmountInCents
                    ? PaymentTransactionStatus.Refunded
                    : PaymentTransactionStatus.PartiallyRefunded;
                result = new PaymentRefundResult(
                    true,
                    transactionId,
                    amountInCents,
                    transaction.RefundedAmountInCents,
                    transaction.Status,
                    transaction.Status == PaymentTransactionStatus.Refunded
                        ? "Die Zahlung wurde vollständig erstattet."
                        : "Die Zahlung wurde teilweise erstattet.");
            }
        }

        PaymentLogContext.LogRefundResult(logger, this, result);
        return Task.FromResult(result);
    }

    public Task<PaymentStatusResult> GetStatusAsync(
        string transactionId,
        CancellationToken cancellationToken = default) {

        cancellationToken.ThrowIfCancellationRequested();
        if (!_transactions.TryGetValue(transactionId, out var transaction)) {
            return Task.FromResult(new PaymentStatusResult(
                false,
                transactionId,
                Guid.Empty,
                0,
                0,
                string.Empty,
                PaymentTransactionStatus.Unknown,
                "Die Zahlungstransaktion wurde beim Anbieter nicht gefunden."));
        }

        lock (transaction.SyncRoot) {
            return Task.FromResult(new PaymentStatusResult(
                true,
                transactionId,
                transaction.OrderId,
                transaction.AmountInCents,
                transaction.RefundedAmountInCents,
                transaction.Currency,
                transaction.Status,
                "Der Transaktionsstatus wurde ermittelt."));
        }
    }

    private sealed class TransactionState(
        Guid orderId,
        long amountInCents,
        string currency) {

        public object SyncRoot { get; } = new();
        public Guid OrderId { get; } = orderId;
        public long AmountInCents { get; } = amountInCents;
        public string Currency { get; } = currency;
        public long RefundedAmountInCents { get; set; }
        public PaymentTransactionStatus Status { get; set; } =
            PaymentTransactionStatus.Succeeded;
    }
}
