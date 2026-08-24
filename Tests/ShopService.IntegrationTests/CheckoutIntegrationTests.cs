using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopService.Checkout;
using ShopService.Orchestration;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;
using Xunit;
using BillingContracts = VstOnlineStore.Contracts.BillingService;
using WarehouseContracts = VstOnlineStore.Contracts.WarehouseService;

namespace ShopService.IntegrationTests;

public sealed class CheckoutIntegrationTests {
    [Fact]
    public async Task OpenApiVerwendetRelativeProxyServerAdresse() {
        var scenario = new CheckoutScenario(CheckoutScenarioMode.Success);
        using var application = new ShopApplicationFactory(scenario);
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync());
        var servers = document.RootElement.GetProperty("servers");
        Assert.Equal(JsonValueKind.Array, servers.ValueKind);
        Assert.Equal("/", servers[0].GetProperty("url").GetString());
    }

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
        Assert.Equal("PayPal", checkout.PaymentProvider);
        Assert.Equal(scenario.InvoiceId.ToString("D"), checkout.InvoiceId);
        Assert.Equal($"/api/invoices/{scenario.InvoiceId:D}/pdf", checkout.InvoiceUrl);
        Assert.Equal(1, scenario.ReservationCalls);
        Assert.Equal(1, scenario.PaymentCalls);
        Assert.Equal(1, scenario.InvoiceEventCalls);
        Assert.Equal(1, scenario.CommitCalls);
        Assert.Equal(0, scenario.ReleaseCalls);
        Assert.Equal(["paypal"], scenario.RequestedProviderKeys);
        Assert.Equal(
            [
                "ReserveCart",
                "ListPaymentProviders",
                "ProcessPayment",
                "CommitCart"
            ],
            scenario.GrpcCalls);
        Assert.All(
            scenario.ReservationIds,
            reservationId => Assert.Equal(checkout.OrderId, reservationId));
        Assert.Contains(
            scenario.AuditEvents,
            audit => audit.EventType == AuditEventType.STOCK_RESERVATION &&
                audit.StatusCode == AuditStatusCode.SUCCESS &&
                HasStringProperty(audit.Payload, "phase", "STOCK_COMMITTED"));
        Assert.Contains(
            scenario.AuditEvents,
            audit => audit.EventType == AuditEventType.ORDER_COMPLETED &&
                audit.StatusCode == AuditStatusCode.SUCCESS &&
                HasStringProperty(audit.Payload, "orderStatus", "COMPLETED"));
        Assert.True(
            FindAuditIndex(scenario, "STOCK_COMMITTED") <
            FindAuditIndex(scenario, "ORDER_COMPLETED"));
    }

    [Fact]
    public async Task Providerwahl_Stripe_WirdBisZumBillingServicePropagiert() {
        var scenario = new CheckoutScenario(CheckoutScenarioMode.Success);
        using var application = new ShopApplicationFactory(scenario);
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            CreateRequest(scenario.ProductId, "stripe"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var checkout = await response.Content.ReadFromJsonAsync<CheckoutResponse>();
        Assert.NotNull(checkout);
        Assert.Equal("Stripe", checkout.PaymentProvider);
        Assert.Equal(["stripe"], scenario.RequestedProviderKeys);
    }

    [Fact]
    public async Task DeaktivierterDemoProvider_WirdVorDerZahlungAbgelehnt() {
        var scenario = new CheckoutScenario(CheckoutScenarioMode.Success);
        using var application = new ShopApplicationFactory(scenario);
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            CreateRequest(scenario.ProductId, "demo"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains("Zahlungsanbieter", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, scenario.ReservationCalls);
        Assert.Equal(0, scenario.PaymentCalls);
        Assert.Equal(1, scenario.ReleaseCalls);
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
        Assert.Equal(0, scenario.CommitCalls);
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
        Assert.Equal(0, scenario.CommitCalls);
        Assert.Equal(1, scenario.ReleaseCalls);
    }

    [Fact]
    public async Task WarehouseNichtErreichbar_ErzeugtErrorLogUndTerminaleSnapshots() {
        var scenario = new CheckoutScenario(CheckoutScenarioMode.WarehouseUnavailable);
        using var application = new ShopApplicationFactory(scenario);
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            CreateRequest(scenario.ProductId));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, scenario.ReservationCalls);
        Assert.Contains(
            scenario.Logs,
            log => log.Level == StructuredLogLevel.ERROR &&
                log.Message == "Downstream service call failed." &&
                HasStringProperty(log.Context, "downstreamService", "WarehouseService") &&
                HasStringProperty(log.Context, "operation", "ReserveCart") &&
                HasStringProperty(log.Context, "grpcStatus", "Unavailable"));
        Assert.Contains(
            scenario.AuditEvents,
            audit => audit.EventType == AuditEventType.STOCK_RESERVATION &&
                audit.StatusCode == AuditStatusCode.FAILURE &&
                HasStringProperty(audit.Payload, "phase", "STOCK_RESERVATION_FAILED"));
        Assert.Contains(
            scenario.AuditEvents,
            audit => audit.EventType == AuditEventType.ORDER_COMPLETED &&
                audit.StatusCode == AuditStatusCode.FAILURE);
    }

    [Fact]
    public async Task WarehouseTimeout_ErzeugtGatewayTimeoutLogUndTerminaleSnapshots() {
        var scenario = new CheckoutScenario(CheckoutScenarioMode.WarehouseTimeout);
        using var application = new ShopApplicationFactory(scenario);
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            CreateRequest(scenario.ProductId));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(1, scenario.ReservationCalls);
        Assert.Equal(0, scenario.PaymentCalls);
        Assert.Contains(
            scenario.Logs,
            log => log.Level == StructuredLogLevel.ERROR &&
                log.Message == "Downstream service call failed." &&
                HasStringProperty(log.Context, "operation", "ReserveCart") &&
                HasStringProperty(log.Context, "grpcStatus", "DeadlineExceeded"));
        Assert.Contains(
            scenario.Logs,
            log => log.Level == StructuredLogLevel.ERROR &&
                log.Message == "Request completed with server error." &&
                HasNumberProperty(log.Context, "statusCode", 504));
        Assert.Contains(
            scenario.AuditEvents,
            audit => audit.EventType == AuditEventType.STOCK_RESERVATION &&
                audit.StatusCode == AuditStatusCode.FAILURE &&
                HasStringProperty(audit.Payload, "phase", "STOCK_RESERVATION_FAILED"));
        Assert.Contains(
            scenario.AuditEvents,
            audit => audit.EventType == AuditEventType.ORDER_COMPLETED &&
                audit.StatusCode == AuditStatusCode.FAILURE);
    }

    [Fact]
    public async Task NichtBetriebsbereiterShop_VerhindertSagaVorDerReservierung() {
        var scenario = new CheckoutScenario(CheckoutScenarioMode.ReadinessUnavailable);
        using var application = new ShopApplicationFactory(scenario);
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            CreateRequest(scenario.ProductId));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("Der Shop ist derzeit nicht betriebsbereit.", problem.Title);
        Assert.Contains("Bestellungen", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, scenario.ReservationCalls);
        Assert.Equal(0, scenario.PaymentCalls);
        Assert.Contains(
            scenario.Logs,
            log => log.Level == StructuredLogLevel.ERROR &&
                log.Message == "Order rejected because the shop is not operational.");
    }

    [Fact]
    public async Task ReadinessTimeout_VerhindertSagaMitGatewayTimeout() {
        var scenario = new CheckoutScenario(CheckoutScenarioMode.ReadinessTimeout);
        using var application = new ShopApplicationFactory(scenario);
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            CreateRequest(scenario.ProductId));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(0, scenario.ReservationCalls);
        Assert.Equal(0, scenario.PaymentCalls);
    }

    [Fact]
    public async Task ServiceStatusEndpunkt_LiefertBetroffeneServicesFuerDasFrontend() {
        var scenario = new CheckoutScenario(CheckoutScenarioMode.ReadinessUnavailable);
        using var application = new ShopApplicationFactory(scenario);
        using var client = application.CreateClient();

        var response = await client.GetAsync("/api/service-statuses");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        var statuses = Assert.IsType<JsonElement>(problem.Extensions["serviceStatuses"]);
        Assert.Equal(JsonValueKind.Array, statuses.ValueKind);
        Assert.Contains(
            statuses.EnumerateArray(),
            status => status.GetProperty("service").GetString() == "AuditService" &&
                !status.GetProperty("available").GetBoolean() &&
                status.GetProperty("failureKind").GetString() == "UNAVAILABLE");
    }

    [Fact]
    public async Task BillingTimeout_NachReservierung_AktiviertSagaKompensation() {
        var scenario = new CheckoutScenario(CheckoutScenarioMode.BillingTimeout);
        using var application = new ShopApplicationFactory(scenario);
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            CreateRequest(scenario.ProductId));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(1, scenario.ReservationCalls);
        Assert.Equal(1, scenario.PaymentCalls);
        Assert.Equal(1, scenario.ReleaseCalls);
        Assert.Contains(
            scenario.AuditEvents,
            audit => audit.EventType == AuditEventType.PAYMENT &&
                audit.StatusCode == AuditStatusCode.FAILURE);
        Assert.Contains(
            scenario.AuditEvents,
            audit => audit.EventType == AuditEventType.STOCK_RELEASE &&
                audit.StatusCode == AuditStatusCode.COMPENSATED);
        Assert.Contains(
            scenario.AuditEvents,
            audit => audit.EventType == AuditEventType.ORDER_COMPLETED &&
                audit.StatusCode == AuditStatusCode.FAILURE);
    }

    [Fact]
    public async Task RechnungseventFehlt_KompensiertZahlungUndReservierung() {
        var scenario = new CheckoutScenario(CheckoutScenarioMode.InvoiceQueueUnavailable);
        using var application = new ShopApplicationFactory(scenario);
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            CreateRequest(scenario.ProductId));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, scenario.ReservationCalls);
        Assert.Equal(1, scenario.PaymentCalls);
        Assert.Equal(0, scenario.InvoiceEventCalls);
        Assert.Equal(1, scenario.RefundCalls);
        Assert.Equal(1, scenario.ReleaseCalls);
        Assert.Equal(0, scenario.CommitCalls);
        Assert.Contains(
            scenario.AuditEvents,
            audit => audit.EventType == AuditEventType.INVOICE &&
                audit.StatusCode == AuditStatusCode.FAILURE &&
                HasStringProperty(audit.Payload, "phase", "INVOICE_QUEUE_FAILED"));
        Assert.Contains(
            scenario.AuditEvents,
            audit => audit.EventType == AuditEventType.PAYMENT &&
                audit.StatusCode == AuditStatusCode.COMPENSATED &&
                HasStringProperty(audit.Payload, "phase", "PAYMENT_REFUNDED"));
    }

    private static CheckoutRequest CreateRequest(
        Guid productId,
        string paymentProviderKey = "paypal") =>
        new(
            [new CheckoutItemRequest(productId.ToString("D"), 2)],
            "kunde@example.com",
            paymentProviderKey);

    private sealed class ShopApplicationFactory(CheckoutScenario scenario)
        : WebApplicationFactory<ShopService.Program> {

        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services => {
                services.RemoveAll<WarehouseContracts.WarehouseCatalog.WarehouseCatalogClient>();
                services.RemoveAll<BillingContracts.BillingOperations.BillingOperationsClient>();
                services.RemoveAll<IAuditEventPublisher>();
                services.RemoveAll<IStructuredLogger>();
                services.RemoveAll<IServiceReadinessService>();

                services.AddSingleton(
                    new WarehouseContracts.WarehouseCatalog.WarehouseCatalogClient(
                        new CheckoutCallInvoker(scenario)));
                services.AddSingleton(
                    new BillingContracts.BillingOperations.BillingOperationsClient(
                        new CheckoutCallInvoker(scenario)));
                services.AddSingleton<IAuditEventPublisher>(
                    new RecordingAuditEventPublisher(scenario));
                services.AddSingleton<IStructuredLogger>(
                    new RecordingStructuredLogger(scenario));
                services.AddSingleton<IServiceReadinessService>(
                    new ScenarioReadinessService(scenario));
            });
        }
    }

    private enum CheckoutScenarioMode {
        Success,
        StockUnavailable,
        PaymentDeclined,
        WarehouseUnavailable,
        WarehouseTimeout,
        BillingTimeout,
        InvoiceQueueUnavailable,
        ReadinessUnavailable,
        ReadinessTimeout
    }

    private sealed class CheckoutScenario(CheckoutScenarioMode mode) {
        public CheckoutScenarioMode Mode { get; } = mode;
        public Guid ProductId { get; } = Guid.Parse("71f3fa9d-8e0c-4f5d-a991-1a48a55d9af8");
        public Guid InvoiceId { get; } = Guid.Parse("cb971c41-4236-4827-95f7-d1009ed82717");
        public int ReservationCalls { get; set; }
        public int PaymentCalls { get; set; }
        public int InvoiceEventCalls { get; set; }
        public int RefundCalls { get; set; }
        public int CommitCalls { get; set; }
        public int ReleaseCalls { get; set; }
        public List<string> GrpcCalls { get; } = [];
        public List<string> ReservationIds { get; } = [];
        public List<string> RequestedProviderKeys { get; } = [];
        public List<RecordedLog> Logs { get; } = [];
        public List<RecordedAuditEvent> AuditEvents { get; } = [];
    }

    private sealed class CheckoutCallInvoker(CheckoutScenario scenario) : CallInvoker {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) {

            scenario.GrpcCalls.Add(method.Name);
            if (request is WarehouseContracts.CartStockRequest stockRequest) {
                scenario.ReservationIds.Add(stockRequest.ReservationId);
            }
            if (request is BillingContracts.PaymentRequest paymentRequest) {
                scenario.RequestedProviderKeys.Add(paymentRequest.ProviderKey);
            }

            if (method.Name == "ReserveCart" &&
                scenario.Mode == CheckoutScenarioMode.WarehouseUnavailable) {
                scenario.ReservationCalls++;
                return Failed<TResponse>(StatusCode.Unavailable);
            }

            if (method.Name == "ReserveCart" &&
                scenario.Mode == CheckoutScenarioMode.WarehouseTimeout) {
                scenario.ReservationCalls++;
                return Failed<TResponse>(StatusCode.DeadlineExceeded);
            }

            if (method.Name == "ProcessPayment" &&
                scenario.Mode == CheckoutScenarioMode.BillingTimeout) {
                scenario.PaymentCalls++;
                return Failed<TResponse>(StatusCode.DeadlineExceeded);
            }

            object response = method.Name switch {
                "ListPaymentProviders" => CreatePaymentProviders(),
                "ReserveCart" => ReserveCart(),
                "CommitCart" => CommitCart(),
                "ReleaseCart" => ReleaseCart(),
                "ProcessPayment" => ProcessPayment((BillingContracts.PaymentRequest)(object)request),
                "RefundPayment" => RefundPayment(),
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
                IsTestMode = true,
                IsActive = false,
                IsEnabled = false
            });
            response.Providers.Add(new BillingContracts.PaymentProviderInfo {
                Key = "paypal",
                Name = "PayPal",
                IsTestMode = true,
                IsActive = true,
                IsEnabled = true
            });
            response.Providers.Add(new BillingContracts.PaymentProviderInfo {
                Key = "stripe",
                Name = "Stripe",
                IsTestMode = true,
                IsActive = false,
                IsEnabled = true
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
                Name = "Testprodukt",
                PriceInCents = 1_299,
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
                Name = "Testprodukt",
                PriceInCents = 1_299,
                AvailableQuantity = 10,
                IsSoldOut = false
            });
            return response;
        }

        private WarehouseContracts.CartStockResponse CommitCart() {
            scenario.CommitCalls++;
            var response = new WarehouseContracts.CartStockResponse {
                Success = true,
                Message = "Reservierung wurde endgültig ausgebucht."
            };
            response.Products.Add(new WarehouseContracts.CartProductStock {
                ProductId = scenario.ProductId.ToString("D"),
                Name = "Testprodukt",
                PriceInCents = 1_299,
                AvailableQuantity = 8,
                IsSoldOut = false
            });
            return response;
        }

        private BillingContracts.PaymentResponse ProcessPayment(
            BillingContracts.PaymentRequest request) {
            scenario.PaymentCalls++;
            var providerName = request.ProviderKey.Equals(
                "stripe",
                StringComparison.OrdinalIgnoreCase)
                ? "Stripe"
                : "PayPal";
            var transactionId = providerName == "Stripe"
                ? "pi_test_INTEGRATION_TRANSACTION"
                : "PAYPAL-TEST-INTEGRATION-TRANSACTION";
            if (scenario.Mode == CheckoutScenarioMode.PaymentDeclined) {
                return new BillingContracts.PaymentResponse {
                    Success = false,
                    Provider = providerName,
                    Message = "Die Zahlung wurde gezielt abgelehnt."
                };
            }

            var invoiceQueued = scenario.Mode != CheckoutScenarioMode.InvoiceQueueUnavailable;
            if (invoiceQueued) {
                scenario.InvoiceEventCalls++;
            }

            return new BillingContracts.PaymentResponse {
                Success = true,
                TransactionId = transactionId,
                Provider = providerName,
                Message = "Die Zahlung wurde bestätigt.",
                InvoiceId = invoiceQueued ? scenario.InvoiceId.ToString("D") : string.Empty,
                InvoiceQueued = invoiceQueued
            };
        }

        private BillingContracts.RefundPaymentResponse RefundPayment() {
            scenario.RefundCalls++;
            return new BillingContracts.RefundPaymentResponse {
                Success = true,
                TransactionId = "PAYPAL-TEST-INTEGRATION-TRANSACTION",
                RefundedAmountInCents = 2_598,
                TotalRefundedAmountInCents = 2_598,
                Status = BillingContracts.PaymentTransactionStatus.Refunded,
                Message = "Die Zahlung wurde vollständig erstattet."
            };
        }

        private static AsyncUnaryCall<TResponse> Completed<TResponse>(TResponse response) =>
            new(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });

        private static AsyncUnaryCall<TResponse> Failed<TResponse>(StatusCode statusCode) {
            var status = new Status(statusCode, "Gezielt simulierter gRPC-Fehler.");
            return new AsyncUnaryCall<TResponse>(
                Task.FromException<TResponse>(new RpcException(status)),
                Task.FromResult(new Metadata()),
                () => status,
                () => new Metadata(),
                () => { });
        }
    }

    private sealed class RecordingAuditEventPublisher(CheckoutScenario scenario)
        : IAuditEventPublisher {

        private static readonly JsonSerializerOptions JsonOptions = new() {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public Task PublishAsync(
            AuditEventType eventType,
            string responsibleService,
            object payload,
            string actor,
            AuditStatusCode statusCode,
            Guid? correlationId = null,
            CancellationToken cancellationToken = default) {

            scenario.AuditEvents.Add(new RecordedAuditEvent(
                eventType,
                statusCode,
                JsonSerializer.SerializeToElement(payload, JsonOptions)));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStructuredLogger(CheckoutScenario scenario)
        : IStructuredLogger {

        private static readonly JsonSerializerOptions JsonOptions = new() {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public void Log(
            StructuredLogLevel logLevel,
            string message,
            object? context = null,
            Exception? exception = null) {

            scenario.Logs.Add(new RecordedLog(
                logLevel,
                message,
                JsonSerializer.SerializeToElement(context, JsonOptions)));
        }
    }

    private sealed class ScenarioReadinessService(CheckoutScenario scenario)
        : IServiceReadinessService {

        public Task<IReadOnlyList<DownstreamServiceStatus>> GetStatusAsync(
            CancellationToken cancellationToken) {

            DownstreamServiceStatus[] statuses = [
                Available("WarehouseService"),
                scenario.Mode == CheckoutScenarioMode.ReadinessTimeout
                    ? Unavailable("BillingService", "TIMEOUT")
                    : Available("BillingService"),
                Available("InvoiceService"),
                scenario.Mode == CheckoutScenarioMode.ReadinessUnavailable
                    ? Unavailable("AuditService", "UNAVAILABLE")
                    : Available("AuditService")
            ];
            return Task.FromResult<IReadOnlyList<DownstreamServiceStatus>>(statuses);
        }

        private static DownstreamServiceStatus Available(string service) =>
            new(service, true, "AVAILABLE", "Betriebsbereit.", 1);

        private static DownstreamServiceStatus Unavailable(
            string service,
            string failureKind) =>
            new(service, false, failureKind, "Nicht betriebsbereit.", 2);
    }

    private static bool HasStringProperty(
        JsonElement context,
        string propertyName,
        string expected) =>
        context.ValueKind == JsonValueKind.Object &&
        context.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        property.GetString() == expected;

    private static bool HasNumberProperty(
        JsonElement context,
        string propertyName,
        int expected) =>
        context.ValueKind == JsonValueKind.Object &&
        context.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt32(out var actual) &&
        actual == expected;

    private static int FindAuditIndex(
        CheckoutScenario scenario,
        string phase) =>
        scenario.AuditEvents.FindIndex(audit =>
            HasStringProperty(audit.Payload, "phase", phase));

    private sealed record RecordedLog(
        StructuredLogLevel Level,
        string Message,
        JsonElement Context);

    private sealed record RecordedAuditEvent(
        AuditEventType EventType,
        AuditStatusCode StatusCode,
        JsonElement Payload);
}
