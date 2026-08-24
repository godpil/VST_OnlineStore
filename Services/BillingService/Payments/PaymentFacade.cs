using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace BillingService.Payments;

public interface IPaymentFacade {
    PaymentProviderDescriptor ActiveProvider { get; }

    IReadOnlyList<PaymentProviderDescriptor> Providers { get; }

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

/// <summary>
/// Einziger Zugriffspunkt des BillingService auf Zahlungsanbieter. Auswahl,
/// Timeout-Behandlung und die Zuordnung einer Transaktion zum ursprünglichen
/// Adapter bleiben vollständig innerhalb der Fassade.
/// </summary>
public sealed class PaymentFacade : IPaymentFacade {
    private readonly IReadOnlyDictionary<string, IPaymentProvider> _providers;
    private readonly IPaymentProvider _activeProvider;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, string> _transactionProviders =
        new(StringComparer.OrdinalIgnoreCase);

    public PaymentFacade(
        IEnumerable<IPaymentProvider> providers,
        IOptions<PaymentProviderOptions> options) {

        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);

        var providerList = providers.ToArray();
        _providers = providerList.ToDictionary(
            provider => provider.Key,
            StringComparer.OrdinalIgnoreCase);

        var configuredOptions = options.Value;
        configuredOptions.Validate();
        _timeout = TimeSpan.FromMilliseconds(configuredOptions.TimeoutMilliseconds);

        if (_providers.Count < 2) {
            throw new InvalidOperationException(
                "Mindestens zwei Zahlungsanbieter müssen registriert sein.");
        }
        if (!_providers.TryGetValue(
                configuredOptions.ActiveProviderKey,
                out _activeProvider!)) {
            throw new InvalidOperationException(
                $"Der konfigurierte aktive Zahlungsanbieter " +
                $"'{configuredOptions.ActiveProviderKey}' ist nicht registriert.");
        }

        Providers = providerList
            .Select(provider => ToDescriptor(
                provider,
                provider.Key.Equals(_activeProvider.Key, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        ActiveProvider = ToDescriptor(_activeProvider, true);
    }

    public PaymentProviderDescriptor ActiveProvider { get; }

    public IReadOnlyList<PaymentProviderDescriptor> Providers { get; }

    public async Task<PaymentChargeResult> ChargeAsync(
        Guid orderId,
        long amountInCents,
        string currency,
        CancellationToken cancellationToken = default) {

        ArgumentOutOfRangeException.ThrowIfEqual(orderId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(amountInCents, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        var result = await ExecuteWithTimeoutAsync(
            _activeProvider,
            "charge",
            providerCancellation => _activeProvider.ChargeAsync(
                orderId,
                amountInCents,
                currency,
                providerCancellation),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.TransactionId)) {
            _transactionProviders[result.TransactionId] = _activeProvider.Key;
        }

        return result;
    }

    public Task<PaymentRefundResult> RefundAsync(
        string transactionId,
        long amountInCents,
        CancellationToken cancellationToken = default) {

        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(amountInCents, 1);

        if (!TryGetTransactionProvider(transactionId, out var provider)) {
            return Task.FromResult(new PaymentRefundResult(
                false,
                transactionId,
                0,
                0,
                PaymentTransactionStatus.Unknown,
                "Die Zahlungstransaktion wurde nicht gefunden."));
        }

        return ExecuteWithTimeoutAsync(
            provider,
            "refund",
            providerCancellation => provider.RefundAsync(
                transactionId,
                amountInCents,
                providerCancellation),
            cancellationToken);
    }

    public Task<PaymentStatusResult> GetStatusAsync(
        string transactionId,
        CancellationToken cancellationToken = default) {

        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);

        if (!TryGetTransactionProvider(transactionId, out var provider)) {
            return Task.FromResult(new PaymentStatusResult(
                false,
                transactionId,
                Guid.Empty,
                0,
                0,
                string.Empty,
                PaymentTransactionStatus.Unknown,
                "Die Zahlungstransaktion wurde nicht gefunden."));
        }

        return ExecuteWithTimeoutAsync(
            provider,
            "getStatus",
            providerCancellation => provider.GetStatusAsync(
                transactionId,
                providerCancellation),
            cancellationToken);
    }

    private bool TryGetTransactionProvider(
        string transactionId,
        out IPaymentProvider provider) {

        provider = null!;
        return _transactionProviders.TryGetValue(transactionId, out var providerKey)
            && _providers.TryGetValue(providerKey, out provider!);
    }

    private async Task<TResult> ExecuteWithTimeoutAsync<TResult>(
        IPaymentProvider provider,
        string operation,
        Func<CancellationToken, Task<TResult>> providerOperation,
        CancellationToken cancellationToken) {

        using var providerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        providerCancellation.CancelAfter(_timeout);

        try {
            return await providerOperation(providerCancellation.Token)
                .WaitAsync(_timeout, cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested
                && providerCancellation.IsCancellationRequested) {
            throw CreateTimeoutException(provider, operation, exception);
        }
        catch (TimeoutException exception) {
            await providerCancellation.CancelAsync();
            throw CreateTimeoutException(provider, operation, exception);
        }
    }

    private TimeoutException CreateTimeoutException(
        IPaymentProvider provider,
        string operation,
        Exception innerException) =>
        new(
            $"Der Zahlungsanbieter '{provider.Key}' hat bei '{operation}' " +
            $"nicht innerhalb von {_timeout.TotalMilliseconds:0} ms geantwortet.",
            innerException);

    private static PaymentProviderDescriptor ToDescriptor(
        IPaymentProvider provider,
        bool isActive) =>
        new(provider.Key, provider.Name, provider.IsTestMode, isActive);
}
