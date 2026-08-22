using Grpc.Core;
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
        });
        builder.Services.AddRabbitMqAuditPublishing(
            builder.Configuration,
            "ShopService");
        builder.Services.AddCorrelationIdPropagation();
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
        builder.Services.AddScoped<ServiceStatusOrchestrator>();
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
                CancellationToken cancellationToken) => {

                if (!featured) {
                    return Results.Problem(
                        detail: "Der Produktfilter 'featured=true' ist erforderlich.",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                try {
                    var response = await warehouse.GetFeaturedProductsAsync(
                        new WarehouseContracts.FeaturedProductsRequest(),
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
                catch (RpcException exception) when (exception.StatusCode == StatusCode.Unavailable) {
                    return DownstreamUnavailable("WarehouseService");
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
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        app.MapGet(
            "/api/payment-providers",
            async (
                BillingContracts.BillingOperations.BillingOperationsClient billing,
                CancellationToken cancellationToken) => {

                try {
                    var response = await billing.ListPaymentProvidersAsync(
                        new BillingContracts.PaymentProvidersRequest(),
                        cancellationToken: cancellationToken);

                    return Results.Ok(response.Providers.Select(provider =>
                        new PaymentProviderResponse(
                            provider.Key,
                            provider.Name,
                            provider.IsTestMode)).ToArray());
                }
                catch (RpcException exception) when (exception.StatusCode == StatusCode.Unavailable) {
                    return DownstreamUnavailable("BillingService");
                }
            })
            .WithName("ListPaymentProviders")
            .WithTags("Payment providers")
            .WithSummary("Zahlungsanbieter abrufen")
            .WithDescription("Liefert die für eine Bestellung auswählbaren Zahlungsanbieter.")
            .Produces<PaymentProviderResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        app.MapPost(
            "/api/orders",
            async (
                CheckoutRequest request,
                HttpContext httpContext,
                CheckoutOrchestrator orchestrator,
                CancellationToken cancellationToken) => {

                if (!CorrelationId.TryGet(httpContext, out var orderId)) {
                    throw new InvalidOperationException(
                        "Für die Bestellung wurde keine Correlation-ID erzeugt.");
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
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Belegt die zentrale Steuerung aller fachlichen Services und dient
        // gleichzeitig als Diagnose-Endpunkt für den Vertical Slice.
        app.MapGet(
            "/api/service-statuses",
            async (ServiceStatusOrchestrator orchestrator, CancellationToken cancellationToken) => {
                try {
                    return Results.Ok(await orchestrator.GetStatusAsync(cancellationToken));
                }
                catch (RpcException) {
                    return Results.Problem(
                        detail: "Mindestens ein Microservice ist nicht erreichbar.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
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
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

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

    private static IResult DownstreamUnavailable(string serviceName) {
        return Results.Problem(
            detail: $"{serviceName} ist nicht erreichbar.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
