using Grpc.Core;
using StoreBackend.Contracts;
using WarehouseModel = StoreBackend.Warehouse.Warehouse;

namespace StoreBackend.Services;

public sealed class WarehouseStorageGrpcService(
    ILogger<WarehouseStorageGrpcService> logger) : WarehouseStorage.WarehouseStorageBase {

    public override Task<AvailableProductsResponse> GetAvailableProducts(
        AvailableProductsRequest request,
        ServerCallContext context) {

        var response = new AvailableProductsResponse();

        response.Products.AddRange(
            WarehouseModel.Instance.GetAvailableProducts().Select(product => new StoredProduct {
                Id = product.Id.ToString(),
                Name = product.Name,
                PriceInCents = decimal.ToInt64(product.Price * 100m),
                Image = product.Image,
                IsAvailable = product.IsAvailable,
                IsReserved = product.IsReserved
            }));

        return Task.FromResult(response);
    }

    public override Task<BackendSelectProductResponse> SelectProduct(
        BackendSelectProductRequest request,
        ServerCallContext context) {

        var hasValidId = Guid.TryParse(request.ProductId, out var productId);
        var success = hasValidId && WarehouseModel.Instance.CanSelectProduct(productId);

        logger.LogInformation(
            "Produktauswahl {ProductId} im Warehouse geprüft. Ergebnis: {Success}",
            request.ProductId,
            success);

        return Task.FromResult(new BackendSelectProductResponse {
            Success = success,
            ProductId = request.ProductId
        });
    }
}
