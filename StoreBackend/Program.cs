using StoreBackend.Services;

namespace StoreBackend;

public class Program {
    public static void Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddGrpc();

        var app = builder.Build();

        // Das StoreBackend ist nur noch über den internen gRPC-Vertrag erreichbar.
        // Der WarehouseService kapselt diesen Zugriff für den ShopService.
        app.MapGrpcService<WarehouseStorageGrpcService>();
        app.MapGet("/", () => "StoreBackend gRPC endpoint");

        app.Run();
    }
}
