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

    public override async Task<CartStockResponse> ReserveCart(
        CartStockRequest request,
        ServerCallContext context) =>
        await ChangeStockAsync(request, reserve: true, context.CancellationToken);

    public override async Task<CartStockResponse> ReleaseCart(
        CartStockRequest request,
        ServerCallContext context) =>
        await ChangeStockAsync(request, reserve: false, context.CancellationToken);

    public override Task<WarehouseStatusResponse> GetStatus(
        WarehouseStatusRequest request,
        ServerCallContext context) {

        return Task.FromResult(new WarehouseStatusResponse {
            Available = true,
            Service = "WarehouseService"
        });
    }

    private async Task<CartStockResponse> ChangeStockAsync(
        CartStockRequest request,
        bool reserve,
        CancellationToken cancellationToken) {

        var backendRequest = new BackendProductQuantitiesRequest();
        backendRequest.Items.AddRange(request.Items.Select(item => new BackendProductQuantity {
            ProductId = item.ProductId,
            Quantity = item.Quantity
        }));

        var backendResponse = reserve
            ? await backend.ReserveProductsAsync(backendRequest, cancellationToken: cancellationToken)
            : await backend.ReleaseProductsAsync(backendRequest, cancellationToken: cancellationToken);

        var response = new CartStockResponse {
            Success = backendResponse.Success,
            Message = backendResponse.Message
        };
        response.Products.AddRange(backendResponse.Products.Select(product => new CartProductStock {
            ProductId = product.ProductId,
            AvailableQuantity = product.AvailableQuantity,
            IsSoldOut = product.IsSoldOut
        }));

        return response;
    }
}
