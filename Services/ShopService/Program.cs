using Grpc.Core;
using ShopService.Checkout;
using ShopService.Orchestration;
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
        app.UseStructuredRequestLogging();

        // Öffentliche REST-Schnittstelle. Nur der ShopService kennt die
        // darunterliegenden fachlichen Microservices.
        app.MapGet(
            "/api/products/featured",
            async (
                WarehouseContracts.WarehouseCatalog.WarehouseCatalogClient warehouse,
                CancellationToken cancellationToken) => {

                try {
                    var response = await warehouse.GetFeaturedProductsAsync(
                        new WarehouseContracts.FeaturedProductsRequest(),
                        cancellationToken: cancellationToken);

                    return Results.Ok(response.Products.Select(product => new {
                        id = product.Id,
                        name = product.Name,
                        price = product.PriceInCents / 100m,
                        image = product.Image,
                        availableQuantity = product.AvailableQuantity,
                        isSoldOut = product.IsSoldOut
                    }));
                }
                catch (RpcException exception) when (exception.StatusCode == StatusCode.Unavailable) {
                    return DownstreamUnavailable("WarehouseService");
                }
            });

        app.MapPost(
            "/api/checkout",
            async (
                CheckoutRequest request,
                CheckoutOrchestrator orchestrator,
                CancellationToken cancellationToken) => {

                var outcome = await orchestrator.CheckoutAsync(request, cancellationToken);
                return Results.Json(outcome.Response, statusCode: outcome.StatusCode);
            });

        // Belegt die zentrale Steuerung aller fachlichen Services und dient
        // gleichzeitig als Diagnose-Endpunkt für den Vertical Slice.
        app.MapGet(
            "/api/services/status",
            async (ServiceStatusOrchestrator orchestrator, CancellationToken cancellationToken) => {
                try {
                    return Results.Ok(await orchestrator.GetStatusAsync(cancellationToken));
                }
                catch (RpcException) {
                    return Results.Problem(
                        title: "Mindestens ein Microservice ist nicht erreichbar",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });

        app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ShopService" }));

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
            title: $"{serviceName} nicht erreichbar",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
