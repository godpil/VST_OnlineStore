using StoreBackend.Contracts;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;
using WarehouseService.GrpcServices;

namespace WarehouseService;

public class Program {
    public static void Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);

        var backendAddress = builder.Configuration["StoreBackend:Address"]
            ?? throw new InvalidOperationException(
                "Die Konfiguration 'StoreBackend:Address' fehlt.");

        builder.Services.AddGrpc();
        builder.Services.AddVstOpenTelemetry(
            builder.Configuration,
            "WarehouseService");
        builder.Services.AddRabbitMqAuditPublishing(
            builder.Configuration,
            "WarehouseService");
        builder.Services.AddCorrelationIdPropagation();
        builder.Services.AddGrpcClient<WarehouseStorage.WarehouseStorageClient>(options => {
            options.Address = new Uri(backendAddress);
        }).AddHttpMessageHandler<CorrelationIdDelegatingHandler>();
        var app = builder.Build();

        app.UseCorrelationId();
        app.UseStructuredRequestLogging();
        app.MapGrpcService<WarehouseCatalogGrpcService>();
        app.MapGet("/", () => "WarehouseService gRPC endpoint");

        app.Run();
    }
}
