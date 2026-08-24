namespace BillingService.Payments;

public sealed class PaymentProviderOptions {
    public const string SectionName = "PaymentProviders";

    public string ActiveProviderKey { get; set; } = "paypal";

    public string[] EnabledProviderKeys { get; set; } = ["paypal", "stripe"];

    public int TimeoutMilliseconds { get; set; } = 5_000;

    public void Validate() {
        ArgumentException.ThrowIfNullOrWhiteSpace(ActiveProviderKey);
        var enabledProviderKeys = GetEnabledProviderKeys();
        if (!enabledProviderKeys.Contains(ActiveProviderKey)) {
            throw new ArgumentException(
                $"Der Standardanbieter '{ActiveProviderKey}' muss aktiviert sein.",
                nameof(ActiveProviderKey));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(TimeoutMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TimeoutMilliseconds, 60_000);
    }

    public bool IsValid() {
        try {
            Validate();
            return true;
        }
        catch (ArgumentException) {
            return false;
        }
    }

    public IReadOnlySet<string> GetEnabledProviderKeys() {
        ArgumentNullException.ThrowIfNull(EnabledProviderKeys);

        var enabledProviderKeys = EnabledProviderKeys
            .Select(key => key?.Trim())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (enabledProviderKeys.Count == 0) {
            throw new ArgumentException(
                "Mindestens ein Zahlungsanbieter muss aktiviert sein.",
                nameof(EnabledProviderKeys));
        }

        return enabledProviderKeys;
    }
}
