using BillingService.Payments;
using Microsoft.Extensions.Options;
using VstOnlineStore.Observability;
using Xunit;

namespace BillingService.UnitTests;

public sealed class PaymentProviderResolverTests {
    private static readonly IStructuredLogger Logger = new NoOpStructuredLogger();

    [Fact]
    public async Task Erfolgsfall_BestaetigtGueltigeZahlung() {
        var provider = new SimulatedPaymentProvider(Logger);
        var facade = CreateFacade([provider]);

        Assert.True(facade.TryResolve("demo", out var selectedProvider));

        var result = await facade.ChargeAsync(
            selectedProvider,
            1_299,
            "EUR",
            "test",
            "ORDER-SUCCESS");

        Assert.True(result.Success);
        Assert.StartsWith("DEMO-", result.TransactionId);
    }

    [Fact]
    public async Task Ablehnung_GibtProviderentscheidungUnveraendertZurueck() {
        var provider = new SimulatedPaymentProvider(Logger);
        var facade = CreateFacade([provider]);

        Assert.True(facade.TryResolve("demo", out var selectedProvider));

        var result = await facade.ChargeAsync(
            selectedProvider,
            1_299,
            "USD",
            "test",
            "ORDER-REJECTED");

        Assert.False(result.Success);
        Assert.Empty(result.TransactionId);
        Assert.Contains("abgelehnt", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("demo")]
    [InlineData("paypal")]
    [InlineData("stripe")]
    public async Task Timeout_WirdFuerJedenAnbieterEinheitlichBehandelt(
        string providerKey) {

        var provider = new WaitingPaymentProvider(providerKey);
        var facade = CreateFacade([provider], providerKey, timeoutMilliseconds: 25);

        Assert.True(facade.TryResolve(providerKey, out var selectedProvider));

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            facade.ChargeAsync(
                selectedProvider,
                1_299,
                "EUR",
                "test",
                $"ORDER-TIMEOUT-{providerKey}"));

        Assert.Contains(providerKey, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Anbieterwechsel_VerwendetKonfiguriertenStandardanbieter() {
        IPaymentProvider[] providers = [
            new SimulatedPaymentProvider(Logger),
            new PayPalPaymentProvider(Logger),
            new StripePaymentProvider(Logger)
        ];
        var facade = CreateFacade(providers, defaultProviderKey: "paypal");

        var resolved = facade.TryResolve(null, out var selectedProvider);

        Assert.True(resolved);
        Assert.Equal("paypal", selectedProvider.Key);
    }

    private static PaymentProviderResolver CreateFacade(
        IEnumerable<IPaymentProvider> providers,
        string defaultProviderKey = "demo",
        int timeoutMilliseconds = 5_000) =>
        new(
            providers,
            Options.Create(new PaymentProviderOptions {
                DefaultProviderKey = defaultProviderKey,
                TimeoutMilliseconds = timeoutMilliseconds
            }));

    private sealed class WaitingPaymentProvider(string key) : IPaymentProvider {
        public string Key => key;
        public string Name => $"Waiting {key}";
        public bool IsTestMode => true;

        public async Task<PaymentProviderResult> ChargeAsync(
            long amountInCents,
            string currency,
            string paymentMethod,
            string reference,
            CancellationToken cancellationToken = default) {

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Der Timeout-Test darf diese Stelle nicht erreichen.");
        }
    }

    private sealed class NoOpStructuredLogger : IStructuredLogger {
        public void Log(
            StructuredLogLevel logLevel,
            string message,
            object? context = null,
            Exception? exception = null) {
        }
    }
}
