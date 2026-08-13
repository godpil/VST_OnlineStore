using Grpc.Core;
using StoreBackend.Contracts;
using VstOnlineStore.Contracts.WarehouseService;

namespace WarehouseService.GrpcServices;

public sealed class WarehouseCatalogGrpcService(
    WarehouseStorage.WarehouseStorageClient backend) : WarehouseCatalog.WarehouseCatalogBase {

    public override async Task<FeaturedProductsResponse> GetFeaturedProducts(
        FeaturedProductsRequest request,
        ServerCallContext context) {

        var backendResponse = await backend.GetAvailableProductsAsync(
            new AvailableProductsRequest(),
            cancellationToken: context.CancellationToken);

        var response = new FeaturedProductsResponse();
        response.Products.AddRange(
            backendResponse.Products.Select(product => new WarehouseProduct {
                Id = product.Id,
                Name = product.Name,
                PriceInCents = product.PriceInCents,
                Image = product.Image
            }));

        return response;
    }

    public override async Task<SelectProductResponse> SelectProduct(
        SelectProductRequest request,
        ServerCallContext context) {

        var backendResponse = await backend.SelectProductAsync(
            new BackendSelectProductRequest { ProductId = request.ProductId },
            cancellationToken: context.CancellationToken);

        return new SelectProductResponse {
            Success = backendResponse.Success,
            ProductId = backendResponse.ProductId
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
