using BillingService.Payments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VstOnlineStore.Observability;
using Xunit;

namespace BillingService.UnitTests;

public sealed class PaymentFacadeTests {
    private static readonly IStructuredLogger Logger = new NoOpStructuredLogger();

    [Fact]
    public async Task Erfolgsfall_BestaetigtGueltigeZahlung() {
        var facade = CreateFacade(CreateProviders());
        var orderId = Guid.NewGuid();

        var result = await facade.ChargeAsync(orderId, 1_299, "EUR");

        Assert.True(result.Success);
        Assert.StartsWith("DEMO-", result.TransactionId);
        Assert.Equal(PaymentTransactionStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Ablehnung_GibtProviderentscheidungUnveraendertZurueck() {
        var facade = CreateFacade(CreateProviders());

        var result = await facade.ChargeAsync(Guid.NewGuid(), 1_299, "USD");

        Assert.False(result.Success);
        Assert.Empty(result.TransactionId);
        Assert.Equal(PaymentTransactionStatus.Failed, result.Status);
        Assert.Contains("abgelehnt", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("demo")]
    [InlineData("paypal")]
    [InlineData("stripe")]
    public async Task Timeout_WirdFuerJedenAnbieterEinheitlichBehandelt(
        string providerKey) {

        IPaymentProvider[] providers = [
            new WaitingPaymentProvider(providerKey),
            new BackupPaymentProvider(providerKey + "-backup")
        ];
        var facade = CreateFacade(
            providers,
            providerKey,
            timeoutMilliseconds: 25);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            facade.ChargeAsync(Guid.NewGuid(), 1_299, "EUR"));

        Assert.Contains(providerKey, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Anbieterwechsel_VerwendetAusschliesslichKonfiguriertenAnbieter() {
        var facade = CreateFacade(CreateProviders(), activeProviderKey: "paypal");

        Assert.Equal("paypal", facade.ActiveProvider.Key);
        Assert.Single(facade.Providers, provider => provider.IsActive);
    }

    [Fact]
    public async Task RefundUndStatus_LaufenAusschliesslichUeberDieFassade() {
        var facade = CreateFacade(CreateProviders(), activeProviderKey: "stripe");
        var orderId = Guid.NewGuid();
        var charge = await facade.ChargeAsync(orderId, 2_000, "EUR");

        var partialRefund = await facade.RefundAsync(charge.TransactionId, 500);
        var partialStatus = await facade.GetStatusAsync(charge.TransactionId);
        var finalRefund = await facade.RefundAsync(charge.TransactionId, 1_500);
        var finalStatus = await facade.GetStatusAsync(charge.TransactionId);

        Assert.True(partialRefund.Success);
        Assert.Equal(PaymentTransactionStatus.PartiallyRefunded, partialStatus.Status);
        Assert.Equal(500, partialStatus.RefundedAmountInCents);
        Assert.True(finalRefund.Success);
        Assert.Equal(PaymentTransactionStatus.Refunded, finalStatus.Status);
        Assert.Equal(2_000, finalStatus.RefundedAmountInCents);
        Assert.Equal(orderId, finalStatus.OrderId);
    }

    [Fact]
    public void AdapterWerdenOhneEinzelregistrierungAutomatischEntdeckt() {
        var services = new ServiceCollection();

        services.AddPaymentFacade();

        var providerRegistrations = services
            .Where(descriptor => descriptor.ServiceType == typeof(IPaymentProvider))
            .ToArray();
        Assert.Equal(3, providerRegistrations.Length);
        Assert.Contains(providerRegistrations, descriptor =>
            descriptor.ImplementationType == typeof(SimulatedPaymentProvider));
        Assert.Contains(providerRegistrations, descriptor =>
            descriptor.ImplementationType == typeof(PayPalPaymentProvider));
        Assert.Contains(providerRegistrations, descriptor =>
            descriptor.ImplementationType == typeof(StripePaymentProvider));
    }

    private static IPaymentProvider[] CreateProviders() => [
        new SimulatedPaymentProvider(Logger),
        new PayPalPaymentProvider(Logger),
        new StripePaymentProvider(Logger)
    ];

    private static IPaymentFacade CreateFacade(
        IEnumerable<IPaymentProvider> providers,
        string activeProviderKey = "demo",
        int timeoutMilliseconds = 5_000) =>
        new PaymentFacade(
            providers,
            Options.Create(new PaymentProviderOptions {
                ActiveProviderKey = activeProviderKey,
                TimeoutMilliseconds = timeoutMilliseconds
            }));

    private sealed class WaitingPaymentProvider(string key) : IPaymentProvider {
        public string Key => key;
        public string Name => $"Waiting {key}";
        public bool IsTestMode => true;

        public async Task<PaymentChargeResult> ChargeAsync(
            Guid orderId,
            long amountInCents,
            string currency,
            CancellationToken cancellationToken = default) {

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException(
                "Der Timeout-Test darf diese Stelle nicht erreichen.");
        }

        public Task<PaymentRefundResult> RefundAsync(
            string transactionId,
            long amountInCents,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentStatusResult> GetStatusAsync(
            string transactionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class BackupPaymentProvider(string key) : IPaymentProvider {
        public string Key => key;
        public string Name => $"Backup {key}";
        public bool IsTestMode => true;

        public Task<PaymentChargeResult> ChargeAsync(
            Guid orderId,
            long amountInCents,
            string currency,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentRefundResult> RefundAsync(
            string transactionId,
            long amountInCents,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentStatusResult> GetStatusAsync(
            string transactionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
