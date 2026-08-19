namespace BillingService.Payments;

/// <summary>
/// Fassade vor den konkreten Zahlungsadaptern. Neue Adapter werden lediglich
/// in der Dependency Injection registriert und stehen danach automatisch zur
/// Auswahl und in der Provider-Liste bereit.
/// </summary>
public sealed class PaymentProviderResolver {
    private readonly IReadOnlyDictionary<string, IPaymentProvider> _providers;
    private readonly IReadOnlyList<IPaymentProvider> _providerList;

    public PaymentProviderResolver(IEnumerable<IPaymentProvider> providers) {
        _providerList = providers.ToArray();
        _providers = _providerList.ToDictionary(
            provider => provider.Key,
            StringComparer.OrdinalIgnoreCase);

        if (_providers.Count == 0) {
            throw new InvalidOperationException(
                "Mindestens ein Zahlungsanbieter muss registriert sein.");
        }
    }

    public IReadOnlyList<IPaymentProvider> Providers => _providerList;

    public bool TryResolve(string? key, out IPaymentProvider provider) {
        var requestedKey = string.IsNullOrWhiteSpace(key) ? "demo" : key.Trim();
        return _providers.TryGetValue(requestedKey, out provider!);
    }
}
