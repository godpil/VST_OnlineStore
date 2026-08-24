using StoreBackend.Application;
using StoreBackend.Application.Ports;
using StoreBackend.Services;
using StoreBackend.Storage;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;

namespace StoreBackend;

public class Program {
    public static async Task Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddGrpc();
        builder.Services.AddVstOpenTelemetry(
            builder.Configuration,
            "StoreBackend");
        builder.Services.AddRabbitMqAuditPublishing(
            builder.Configuration,
            "StoreBackend");
        builder.Services.AddSingleton<JsonWarehouseRepository>(services => {
            var configuredPath = builder.Configuration["WarehouseData:FilePath"]
                ?? "Data/warehouse-products.json";
            var dataFilePath = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.GetFullPath(Path.Combine(
                    builder.Environment.ContentRootPath,
                    configuredPath));
            var configuredReservationPath = builder.Configuration["WarehouseData:ReservationFilePath"];
            var reservationFilePath = string.IsNullOrWhiteSpace(configuredReservationPath)
                ? Path.Combine(
                    Path.GetDirectoryName(dataFilePath) ?? string.Empty,
                    $"{Path.GetFileNameWithoutExtension(dataFilePath)}.reservations.json")
                : Path.IsPathRooted(configuredReservationPath)
                    ? configuredReservationPath
                    : Path.GetFullPath(Path.Combine(
                        builder.Environment.ContentRootPath,
                        configuredReservationPath));

            return new JsonWarehouseRepository(
                dataFilePath,
                reservationFilePath,
                services.GetRequiredService<IStructuredLogger>());
        });
        builder.Services.AddSingleton<IWarehouseRepository>(services =>
            services.GetRequiredService<JsonWarehouseRepository>());
        builder.Services.AddSingleton<WarehouseApplicationService>();

        var app = builder.Build();

        app.UseCorrelationId();
        app.UseStructuredRequestLogging();

        // Vor dem ersten Zugriff über WarehouseService muss der aktuelle
        // Lagerbestand vollständig von der Festplatte geladen sein.
        try {
            await app.Services
                .GetRequiredService<JsonWarehouseRepository>()
                .ReadFromDiskAsync();
        }
        catch (Exception exception) {
            app.Services
                .GetRequiredService<IStructuredLogger>()
                .Error(
                    "Warehouse data initialization failed.",
                    new {
                        operation = "ReadFromDisk",
                        exceptionType = exception.GetType().FullName,
                        exceptionMessage = exception.Message
                    },
                    exception);
            throw;
        }

        app.MapGrpcService<WarehouseStorageGrpcService>();
        app.MapGet("/", () => "StoreBackend gRPC endpoint");

        await app.RunAsync();
    }
}
