using StoreBackend.Application;
using StoreBackend.Application.Ports;
using StoreBackend.Services;
using StoreBackend.Storage;

namespace StoreBackend;

public class Program {
    public static async Task Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddGrpc();
        builder.Services.AddSingleton<JsonWarehouseRepository>(services => {
            var configuredPath = builder.Configuration["WarehouseData:FilePath"]
                ?? "Data/warehouse-products.json";
            var dataFilePath = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.GetFullPath(Path.Combine(
                    builder.Environment.ContentRootPath,
                    configuredPath));

            return new JsonWarehouseRepository(
                dataFilePath,
                services.GetRequiredService<ILogger<JsonWarehouseRepository>>());
        });
        builder.Services.AddSingleton<IWarehouseRepository>(services =>
            services.GetRequiredService<JsonWarehouseRepository>());
        builder.Services.AddSingleton<WarehouseApplicationService>();

        var app = builder.Build();

        // Vor dem ersten Zugriff über WarehouseService muss der aktuelle
        // Lagerbestand vollständig von der Festplatte geladen sein.
        await app.Services
            .GetRequiredService<JsonWarehouseRepository>()
            .ReadFromDiskAsync();

        app.MapGrpcService<WarehouseStorageGrpcService>();
        app.MapGet("/", () => "StoreBackend gRPC endpoint");

        await app.RunAsync();
    }
}
