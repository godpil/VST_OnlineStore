namespace BillingService.Payments;

public sealed class PaymentProviderOptions {
    public const string SectionName = "PaymentProviders";

    public string ActiveProviderKey { get; set; } = "demo";

    public int TimeoutMilliseconds { get; set; } = 5_000;

    public void Validate() {
        ArgumentException.ThrowIfNullOrWhiteSpace(ActiveProviderKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(TimeoutMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TimeoutMilliseconds, 60_000);
    }

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(ActiveProviderKey)
        && TimeoutMilliseconds is >= 1 and <= 60_000;
}
