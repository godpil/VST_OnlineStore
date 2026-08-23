namespace BillingService.Payments;

public sealed class PaymentProviderOptions {
    public const string SectionName = "PaymentProviders";

    public string DefaultProviderKey { get; set; } = "demo";

    public int TimeoutMilliseconds { get; set; } = 5_000;

    public void Validate() {
        ArgumentException.ThrowIfNullOrWhiteSpace(DefaultProviderKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(TimeoutMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TimeoutMilliseconds, 60_000);
    }
}
