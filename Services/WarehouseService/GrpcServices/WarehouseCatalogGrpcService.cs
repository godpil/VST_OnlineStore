using Grpc.Core;
using StoreBackend.Contracts;
using VstOnlineStore.Contracts.WarehouseService;

namespace WarehouseService.GrpcServices;

/// <summary>
/// Öffentliche Warehouse-Grenze. Der Zugriff auf StoreBackend erfolgt
/// ausschließlich über den internen gRPC-Vertrag.
/// </summary>
public sealed class WarehouseCatalogGrpcService(
    WarehouseStorage.WarehouseStorageClient backend) : WarehouseCatalog.WarehouseCatalogBase {

    public override async Task<FeaturedProductsResponse> GetFeaturedProducts(
        FeaturedProductsRequest request,
        ServerCallContext context) {

        var backendResponse = await backend.GetProductsAsync(
            new BackendProductsRequest(),
            cancellationToken: context.CancellationToken);
        var response = new FeaturedProductsResponse();

        response.Products.AddRange(
            backendResponse.Products.Select(product => new WarehouseProduct {
                Id = product.Id,
                Name = product.Name,
                PriceInCents = product.PriceInCents,
                Image = product.Image,
                AvailableQuantity = product.AvailableQuantity,
                IsSoldOut = product.IsSoldOut
            }));

        return response;
    }

    public override async Task<SelectProductResponse> SelectProduct(
        SelectProductRequest request,
        ServerCallContext context) {

        var backendResponse = await backend.ReserveProductAsync(
            new BackendReserveProductRequest {
                ProductId = request.ProductId,
                Quantity = request.Quantity > 0 ? request.Quantity : 1
            },
            cancellationToken: context.CancellationToken);

        return new SelectProductResponse {
            Success = backendResponse.Success,
            ProductId = backendResponse.ProductId,
            AvailableQuantity = backendResponse.AvailableQuantity,
            IsSoldOut = backendResponse.IsSoldOut,
            Message = backendResponse.Message
        };
    }

    public override Task<WarehouseStatusResponse> GetStatus(
        WarehouseStatusRequest request,
        ServerCallContext context) {

        return Task.FromResult(new WarehouseStatusResponse {
            Available = true,
            Service = "WarehouseService"
        });
    }
}
