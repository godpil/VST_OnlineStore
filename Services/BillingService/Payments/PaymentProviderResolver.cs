using Microsoft.Extensions.Options;

namespace BillingService.Payments;

/// <summary>
/// Fassade vor den konkreten Zahlungsadaptern. Neue Adapter werden lediglich
/// in der Dependency Injection registriert und stehen danach automatisch zur
/// Auswahl und in der Provider-Liste bereit.
/// </summary>
public sealed class PaymentProviderResolver {
    private readonly IReadOnlyDictionary<string, IPaymentProvider> _providers;
    private readonly IReadOnlyList<IPaymentProvider> _providerList;
    private readonly PaymentProviderOptions _options;

    public PaymentProviderResolver(
        IEnumerable<IPaymentProvider> providers,
        IOptions<PaymentProviderOptions> options) {

        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);

        _providerList = providers.ToArray();
        _providers = _providerList.ToDictionary(
            provider => provider.Key,
            StringComparer.OrdinalIgnoreCase);
        _options = options.Value;
        _options.Validate();

        if (_providers.Count == 0) {
            throw new InvalidOperationException(
                "Mindestens ein Zahlungsanbieter muss registriert sein.");
        }
        if (!_providers.ContainsKey(_options.DefaultProviderKey)) {
            throw new InvalidOperationException(
                $"Der konfigurierte Standard-Zahlungsanbieter '{_options.DefaultProviderKey}' ist nicht registriert.");
        }
    }

    public IReadOnlyList<IPaymentProvider> Providers => _providerList;

    public bool TryResolve(string? key, out IPaymentProvider provider) {
        var requestedKey = string.IsNullOrWhiteSpace(key)
            ? _options.DefaultProviderKey
            : key.Trim();
        return _providers.TryGetValue(requestedKey, out provider!);
    }

    public async Task<PaymentProviderResult> ChargeAsync(
        IPaymentProvider provider,
        long amountInCents,
        string currency,
        string paymentMethod,
        string reference,
        CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(provider);

        var timeout = TimeSpan.FromMilliseconds(_options.TimeoutMilliseconds);
        using var providerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        providerCancellation.CancelAfter(timeout);

        var providerTask = provider.ChargeAsync(
            amountInCents,
            currency,
            paymentMethod,
            reference,
            providerCancellation.Token);

        try {
            return await providerTask.WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested
                && providerCancellation.IsCancellationRequested) {
            throw CreateTimeoutException(provider, timeout, exception);
        }
        catch (TimeoutException exception) {
            await providerCancellation.CancelAsync();
            throw CreateTimeoutException(provider, timeout, exception);
        }
    }

    private static TimeoutException CreateTimeoutException(
        IPaymentProvider provider,
        TimeSpan timeout,
        Exception innerException) =>
        new(
            $"Der Zahlungsanbieter '{provider.Key}' hat nicht innerhalb von {timeout.TotalMilliseconds:0} ms geantwortet.",
            innerException);
}
