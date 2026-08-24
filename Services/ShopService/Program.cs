using Grpc.Core;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using ShopService.Api;
using ShopService.Checkout;
using ShopService.Orchestration;
using ShopService.Queries;
using AuditContracts = VstOnlineStore.Contracts.AuditService;
using BillingContracts = VstOnlineStore.Contracts.BillingService;
using InvoiceContracts = VstOnlineStore.Contracts.InvoiceService;
using WarehouseContracts = VstOnlineStore.Contracts.WarehouseService;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;

namespace ShopService;

public class Program {
    public static void Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddVstOpenTelemetry(
            builder.Configuration,
            "ShopService");
        builder.Services.AddVstProblemDetails();
        builder.Services.AddOpenApi(options => {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            options.AddDocumentTransformer((document, _, _) => {
                // Die Swagger-UI wird am öffentlichen StoreProxy ausgeliefert.
                // Eine relative Server-URL hält sämtliche "Try it out"-Aufrufe
                // auf genau diesem Ursprung und verhindert direkte Browser-
                // Zugriffe auf den internen ShopService-Port.
                document.Servers = [new OpenApiServer { Url = "/" }];
                return Task.CompletedTask;
            });
        });
        builder.Services.AddRabbitMqAuditPublishing(
            builder.Configuration,
            "ShopService");
        builder.Services.AddCorrelationIdPropagation();
        builder.Services
            .AddOptions<ShopServiceTimeoutOptions>()
            .Bind(builder.Configuration.GetSection(ShopServiceTimeoutOptions.SectionName))
            .Validate(options => options.IsValid(), "Alle Downstream-Timeouts müssen positiv sein.")
            .ValidateOnStart();
        builder.Services.AddGrpcClient<WarehouseContracts.WarehouseCatalog.WarehouseCatalogClient>(options => {
            options.Address = GetServiceAddress(builder.Configuration, "WarehouseService");
        }).AddHttpMessageHandler<CorrelationIdDelegatingHandler>();
        builder.Services.AddGrpcClient<BillingContracts.BillingOperations.BillingOperationsClient>(options => {
            options.Address = GetServiceAddress(builder.Configuration, "BillingService");
        }).AddHttpMessageHandler<CorrelationIdDelegatingHandler>();
        builder.Services.AddGrpcClient<InvoiceContracts.InvoiceOperations.InvoiceOperationsClient>(options => {
            options.Address = GetServiceAddress(builder.Configuration, "InvoiceService");
        }).AddHttpMessageHandler<CorrelationIdDelegatingHandler>();
        builder.Services.AddGrpcClient<AuditContracts.AuditOperations.AuditOperationsClient>(options => {
            options.Address = GetServiceAddress(builder.Configuration, "AuditService");
        }).AddHttpMessageHandler<CorrelationIdDelegatingHandler>();
        builder.Services.AddScoped<IServiceReadinessService, ServiceStatusOrchestrator>();
        builder.Services.AddScoped<AuditSnapshotRecorder>();
        builder.Services.AddScoped<CheckoutOrchestrator>();

        var app = builder.Build();

        app.UseCorrelationId();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseStructuredRequestLogging();
        app.UseSwaggerUI(options => {
            options.DocumentTitle = "VST OnlineStore API";
            options.RoutePrefix = "swagger";
            options.SwaggerEndpoint("/openapi/v1.json", "VST OnlineStore API v1");
        });

        app.MapOpenApi("/openapi/{documentName}.json");

        // Öffentliche REST-Schnittstelle. Nur der ShopService kennt die
        // darunterliegenden fachlichen Microservices.
        app.MapGet(
            "/api/products",
            async (
                bool featured,
                WarehouseContracts.WarehouseCatalog.WarehouseCatalogClient warehouse,
                IOptions<ShopServiceTimeoutOptions> configuredTimeouts,
                IStructuredLogger logger,
                CancellationToken cancellationToken) => {

                if (!featured) {
                    return Results.Problem(
                        detail: "Der Produktfilter 'featured=true' ist erforderlich.",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                try {
                    var response = await warehouse.GetFeaturedProductsAsync(
                        new WarehouseContracts.FeaturedProductsRequest(),
                        deadline: DateTime.UtcNow.Add(configuredTimeouts.Value.CatalogQuery),
                        cancellationToken: cancellationToken);

                    return Results.Ok(response.Products.Select(product =>
                        new ProductResponse(
                            product.Id,
                            product.Name,
                            product.PriceInCents / 100m,
                            product.Image,
                            product.AvailableQuantity,
                            product.IsSoldOut)).ToArray());
                }
                catch (RpcException exception)
                    when (!IsRequestCancellation(exception, cancellationToken)) {
                    return DownstreamFailure(
                        "WarehouseService",
                        "GetFeaturedProducts",
                        exception,
                        logger);
                }
            })
            .WithName("ListFeaturedProducts")
            .WithTags("Products")
            .WithSummary("Ausgewählte Produkte abrufen")
            .WithDescription(
                "Liefert den öffentlichen Produktkatalog. Der Query-Parameter " +
                "featured muss den Wert true haben.")
            .Produces<ProductResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        app.MapGet(
            "/api/payment-providers",
            async (
                BillingContracts.BillingOperations.BillingOperationsClient billing,
                IOptions<ShopServiceTimeoutOptions> configuredTimeouts,
                IStructuredLogger logger,
                CancellationToken cancellationToken) => {

                try {
                    var response = await billing.ListPaymentProvidersAsync(
                        new BillingContracts.PaymentProvidersRequest(),
                        deadline: DateTime.UtcNow.Add(configuredTimeouts.Value.CatalogQuery),
                        cancellationToken: cancellationToken);

                    return Results.Ok(response.Providers.Select(provider =>
                        new PaymentProviderResponse(
                            provider.Key,
                            provider.Name,
                            provider.IsTestMode,
                            provider.IsActive)).ToArray());
                }
                catch (RpcException exception)
                    when (!IsRequestCancellation(exception, cancellationToken)) {
                    return DownstreamFailure(
                        "BillingService",
                        "ListPaymentProviders",
                        exception,
                        logger);
                }
            })
            .WithName("ListPaymentProviders")
            .WithTags("Payment providers")
            .WithSummary("Zahlungsanbieter abrufen")
            .WithDescription(
                "Liefert die registrierten Zahlungsanbieter und kennzeichnet den " +
                "zentral konfigurierten aktiven Anbieter.")
            .Produces<PaymentProviderResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        app.MapPost(
            "/api/orders",
            async (
                CheckoutRequest request,
                HttpContext httpContext,
                IServiceReadinessService readiness,
                CheckoutOrchestrator orchestrator,
                IStructuredLogger logger,
                CancellationToken cancellationToken) => {

                if (!CorrelationId.TryGet(httpContext, out var orderId)) {
                    throw new InvalidOperationException(
                        "Für die Bestellung wurde keine Correlation-ID erzeugt.");
                }

                var serviceStatuses = await readiness.GetStatusAsync(cancellationToken);
                if (!ServiceReadiness.IsOperational(serviceStatuses)) {
                    logger.Error(
                        "Order rejected because the shop is not operational.",
                        new {
                            orderId,
                            unavailableServices = serviceStatuses
                                .Where(status => !status.Available)
                                .Select(status => new {
                                    status.Service,
                                    status.FailureKind,
                                    status.Message,
                                    status.DurationMs
                                })
                                .ToArray()
                        });
                    return ShopUnavailable(serviceStatuses, orderId);
                }

                var outcome = await orchestrator.CheckoutAsync(
                    request,
                    orderId,
                    cancellationToken);
                if (outcome.Response.Success) {
                    return Results.Json(
                        outcome.Response,
                        statusCode: StatusCodes.Status201Created);
                }

                return Results.Problem(
                    detail: outcome.Response.Message,
                    statusCode: outcome.StatusCode,
                    extensions: new Dictionary<string, object?> {
                        ["orderId"] = outcome.Response.OrderId,
                        ["total"] = outcome.Response.Total,
                        ["currency"] = outcome.Response.Currency
                    });
            })
            .WithName("CreateOrder")
            .WithTags("Orders")
            .WithSummary("Bestellung anlegen")
            .WithDescription(
                "Validiert den Warenkorb, reserviert den Bestand und führt die Zahlung aus. " +
                "Die zurückgegebene orderId entspricht der Correlation-ID des Vorgangs.")
            .Accepts<CheckoutRequest>("application/json")
            .Produces<CheckoutResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        // Belegt die zentrale Steuerung aller fachlichen Services und dient
        // gleichzeitig als Diagnose-Endpunkt für den Vertical Slice.
        app.MapGet(
            "/api/service-statuses",
            async (IServiceReadinessService readiness, CancellationToken cancellationToken) => {
                var serviceStatuses = await readiness.GetStatusAsync(cancellationToken);
                if (ServiceReadiness.IsOperational(serviceStatuses)) {
                    return Results.Ok(serviceStatuses);
                }

                return ShopUnavailable(serviceStatuses);
            })
            .WithName("ListServiceStatuses")
            .WithTags("Service statuses")
            .WithSummary("Status der fachlichen Services abrufen")
            .WithDescription(
                "Prüft WarehouseService, BillingService, InvoiceService und AuditService.")
            .Produces<DownstreamServiceStatus[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        // StoreProxy veröffentlicht diese URLs, kommuniziert jedoch nur per
        // YARP mit dem ShopService. Die gRPC-Orchestrierung bleibt hier.
        app.MapInvoiceQueryEndpoints();
        app.MapAuditQueryEndpoints();

        app.MapGet(
                "/health",
                () => Results.Ok(new HealthResponse("ok", "ShopService")))
            .WithName("GetShopHealth")
            .WithTags("Health")
            .WithSummary("Health-Status des ShopService abrufen")
            .Produces<HealthResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.Run();
    }

    private static Uri GetServiceAddress(IConfiguration configuration, string serviceName) {
        var address = configuration[$"Services:{serviceName}:Address"]
            ?? throw new InvalidOperationException(
                $"Die Konfiguration 'Services:{serviceName}:Address' fehlt.");

        return new Uri(address);
    }

    private static IResult DownstreamFailure(
        string serviceName,
        string operation,
        RpcException exception,
        IStructuredLogger logger) {

        logger.Error(
            "Downstream service call failed.",
            new {
                downstreamService = serviceName,
                operation,
                grpcStatus = exception.StatusCode.ToString(),
                grpcDetail = exception.Status.Detail,
                exceptionType = exception.GetType().FullName,
                exceptionMessage = exception.Message
            },
            exception);

        var (statusCode, detail) = MapDownstreamFailure(serviceName, exception);
        return Results.Problem(detail: detail, statusCode: statusCode);
    }

    private static IResult ShopUnavailable(
        IReadOnlyList<DownstreamServiceStatus> serviceStatuses,
        Guid? orderId = null) {

        var statusCode = ServiceReadiness.GetHttpStatusCode(serviceStatuses);
        var unavailableServices = serviceStatuses
            .Where(status => !status.Available)
            .ToArray();
        var extensions = new Dictionary<string, object?> {
            ["shopOperational"] = false,
            ["serviceStatuses"] = serviceStatuses,
            ["retryable"] = true
        };
        if (orderId.HasValue) {
            extensions["orderId"] = orderId.Value.ToString("D");
        }

        var reason = unavailableServices.Any(status => status.FailureKind == "TIMEOUT")
            ? "Mindestens ein erforderlicher Service hat nicht rechtzeitig geantwortet."
            : "Mindestens ein erforderlicher Service ist derzeit nicht verfügbar.";
        return Results.Problem(
            title: "Der Shop ist derzeit nicht betriebsbereit.",
            detail: $"{reason} Bestellungen sind vorübergehend deaktiviert.",
            statusCode: statusCode,
            extensions: extensions);
    }

    private static (int StatusCode, string Detail) MapDownstreamFailure(
        string serviceName,
        RpcException exception) =>
        exception.StatusCode switch {
            StatusCode.Unavailable => (
                StatusCodes.Status503ServiceUnavailable,
                $"{serviceName} ist nicht erreichbar."),
            StatusCode.DeadlineExceeded => (
                StatusCodes.Status504GatewayTimeout,
                $"{serviceName} hat nicht rechtzeitig geantwortet."),
            _ => (
                StatusCodes.Status502BadGateway,
                $"{serviceName} konnte die Anfrage nicht verarbeiten.")
        };

    private static bool IsRequestCancellation(
        RpcException exception,
        CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested &&
        exception.StatusCode == StatusCode.Cancelled;
}
