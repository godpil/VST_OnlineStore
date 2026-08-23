using System.Net;
using System.Net.Http.Json;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopService.Checkout;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;
using Xunit;
using BillingContracts = VstOnlineStore.Contracts.BillingService;
using WarehouseContracts = VstOnlineStore.Contracts.WarehouseService;

namespace ShopService.IntegrationTests;

public sealed class CheckoutIntegrationTests {
    [Fact]
    public async Task HappyPath_ErzeugtErfolgreicheBestellungMitRechnung() {
        var scenario = new CheckoutScenario(CheckoutScenarioMode.Success);
        using var application = new ShopApplicationFactory(scenario);
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            CreateRequest(scenario.ProductId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var checkout = await response.Content.ReadFromJsonAsync<CheckoutResponse>();
        Assert.NotNull(checkout);
        Assert.True(checkout.Success);
        Assert.Equal(25.98m, checkout.Total);
        Assert.Equal("Holzwerk DemoPay", checkout.PaymentProvider);
        Assert.Equal(scenario.InvoiceId.ToString("D"), checkout.InvoiceId);
        Assert.Equal($"/api/invoices/{scenario.InvoiceId:D}/pdf", checkout.InvoiceUrl);
        Assert.Equal(1, scenario.ReservationCalls);
        Assert.Equal(1, scenario.PaymentCalls);
        Assert.Equal(0, scenario.ReleaseCalls);
    }

    [Fact]
    public async Task Fail1_UnzureichenderBestand_VerhindertDieZahlung() {
        var scenario = new CheckoutScenario(CheckoutScenarioMode.StockUnavailable);
        using var application = new ShopApplicationFactory(scenario);
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            CreateRequest(scenario.ProductId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains("Bestand", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, scenario.ReservationCalls);
        Assert.Equal(0, scenario.PaymentCalls);
        Assert.Equal(0, scenario.ReleaseCalls);
    }

    [Fact]
    public async Task Fail2_Zahlungsablehnung_GibtReservierungWiederFrei() {
        var scenario = new CheckoutScenario(CheckoutScenarioMode.PaymentDeclined);
        using var application = new ShopApplicationFactory(scenario);
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            CreateRequest(scenario.ProductId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains("abgelehnt", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, scenario.ReservationCalls);
        Assert.Equal(1, scenario.PaymentCalls);
        Assert.Equal(1, scenario.ReleaseCalls);
    }

    private static CheckoutRequest CreateRequest(Guid productId) =>
        new(
            [new CheckoutItemRequest(productId.ToString("D"), 2)],
            "demo",
            "kunde@example.com");

    private sealed class ShopApplicationFactory(CheckoutScenario scenario)
        : WebApplicationFactory<ShopService.Program> {

        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services => {
                services.RemoveAll<WarehouseContracts.WarehouseCatalog.WarehouseCatalogClient>();
                services.RemoveAll<BillingContracts.BillingOperations.BillingOperationsClient>();
                services.RemoveAll<IAuditEventPublisher>();
                services.RemoveAll<IStructuredLogger>();

                services.AddSingleton(
                    new WarehouseContracts.WarehouseCatalog.WarehouseCatalogClient(
                        new CheckoutCallInvoker(scenario)));
                services.AddSingleton(
                    new BillingContracts.BillingOperations.BillingOperationsClient(
                        new CheckoutCallInvoker(scenario)));
                services.AddSingleton<IAuditEventPublisher, NoOpAuditEventPublisher>();
                services.AddSingleton<IStructuredLogger, NoOpStructuredLogger>();
            });
        }
    }

    private enum CheckoutScenarioMode {
        Success,
        StockUnavailable,
        PaymentDeclined
    }

    private sealed class CheckoutScenario(CheckoutScenarioMode mode) {
        public CheckoutScenarioMode Mode { get; } = mode;
        public Guid ProductId { get; } = Guid.Parse("71f3fa9d-8e0c-4f5d-a991-1a48a55d9af8");
        public Guid InvoiceId { get; } = Guid.Parse("cb971c41-4236-4827-95f7-d1009ed82717");
        public int ReservationCalls { get; set; }
        public int PaymentCalls { get; set; }
        public int ReleaseCalls { get; set; }
    }

    private sealed class CheckoutCallInvoker(CheckoutScenario scenario) : CallInvoker {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) {

            object response = method.Name switch {
                "ListPaymentProviders" => CreatePaymentProviders(),
                "GetFeaturedProducts" => CreateCatalog(),
                "ReserveCart" => ReserveCart(),
                "ReleaseCart" => ReleaseCart(),
                "ProcessPayment" => ProcessPayment(),
                _ => throw new InvalidOperationException(
                    $"Unerwarteter gRPC-Aufruf im Integrationstest: {method.FullName}")
            };

            return Completed((TResponse)response);
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) =>
            throw new NotSupportedException("Der Bestellprozess verwendet ausschließlich asynchrone Aufrufe.");

        public override AsyncClientStreamingCall<TRequest, TResponse>
            AsyncClientStreamingCall<TRequest, TResponse>(
                Method<TRequest, TResponse> method,
                string? host,
                CallOptions options) =>
            throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse>
            AsyncServerStreamingCall<TRequest, TResponse>(
                Method<TRequest, TResponse> method,
                string? host,
                CallOptions options,
                TRequest request) =>
            throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse>
            AsyncDuplexStreamingCall<TRequest, TResponse>(
                Method<TRequest, TResponse> method,
                string? host,
                CallOptions options) =>
            throw new NotSupportedException();

        private BillingContracts.PaymentProvidersResponse CreatePaymentProviders() {
            var response = new BillingContracts.PaymentProvidersResponse();
            response.Providers.Add(new BillingContracts.PaymentProviderInfo {
                Key = "demo",
                Name = "Holzwerk DemoPay",
                IsTestMode = true
            });
            return response;
        }

        private WarehouseContracts.FeaturedProductsResponse CreateCatalog() {
            var response = new WarehouseContracts.FeaturedProductsResponse();
            response.Products.Add(new WarehouseContracts.WarehouseProduct {
                Id = scenario.ProductId.ToString("D"),
                Name = "Testprodukt",
                PriceInCents = 1_299,
                AvailableQuantity = 10,
                IsSoldOut = false
            });
            return response;
        }

        private WarehouseContracts.CartStockResponse ReserveCart() {
            scenario.ReservationCalls++;
            var success = scenario.Mode != CheckoutScenarioMode.StockUnavailable;
            var response = new WarehouseContracts.CartStockResponse {
                Success = success,
                Message = success
                    ? "Bestand wurde reserviert."
                    : "Nicht genügend Bestand vorhanden."
            };
            response.Products.Add(new WarehouseContracts.CartProductStock {
                ProductId = scenario.ProductId.ToString("D"),
                AvailableQuantity = success ? 8 : 1,
                IsSoldOut = false
            });
            return response;
        }

        private WarehouseContracts.CartStockResponse ReleaseCart() {
            scenario.ReleaseCalls++;
            var response = new WarehouseContracts.CartStockResponse {
                Success = true,
                Message = "Reservierung wurde aufgehoben."
            };
            response.Products.Add(new WarehouseContracts.CartProductStock {
                ProductId = scenario.ProductId.ToString("D"),
                AvailableQuantity = 10,
                IsSoldOut = false
            });
            return response;
        }

        private BillingContracts.PaymentResponse ProcessPayment() {
            scenario.PaymentCalls++;
            if (scenario.Mode == CheckoutScenarioMode.PaymentDeclined) {
                return new BillingContracts.PaymentResponse {
                    Success = false,
                    Provider = "Holzwerk DemoPay",
                    Message = "Die Zahlung wurde gezielt abgelehnt."
                };
            }

            return new BillingContracts.PaymentResponse {
                Success = true,
                TransactionId = "DEMO-INTEGRATION-TRANSACTION",
                Provider = "Holzwerk DemoPay",
                Message = "Die Zahlung wurde bestätigt.",
                InvoiceId = scenario.InvoiceId.ToString("D"),
                InvoiceQueued = true
            };
        }

        private static AsyncUnaryCall<TResponse> Completed<TResponse>(TResponse response) =>
            new(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
    }

    private sealed class NoOpAuditEventPublisher : IAuditEventPublisher {
        public Task PublishAsync(
            AuditEventType eventType,
            string responsibleService,
            object payload,
            string actor,
            AuditStatusCode statusCode,
            Guid? correlationId = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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
