using StoreBackend.Contracts;
using VstOnlineStore.StoreBackend.Abstractions;
using WarehouseService.Backend;
using WarehouseService.GrpcServices;

namespace WarehouseService;

public class Program {
    public static void Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);

        var backendAddress = builder.Configuration["StoreBackend:Address"]
            ?? throw new InvalidOperationException(
                "Die Konfiguration 'StoreBackend:Address' fehlt.");

        builder.Services.AddGrpc();
        builder.Services.AddGrpcClient<WarehouseStorage.WarehouseStorageClient>(options => {
            options.Address = new Uri(backendAddress);
        });
        builder.Services.AddScoped<IStoreBackend, GrpcStoreBackendAdapter>();

        var app = builder.Build();

        app.MapGrpcService<WarehouseCatalogGrpcService>();
        app.MapGet("/", () => "WarehouseService gRPC endpoint");

        app.Run();
    }
}
