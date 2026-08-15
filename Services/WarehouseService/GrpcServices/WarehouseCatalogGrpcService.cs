using Grpc.Core;
using VstOnlineStore.Contracts.WarehouseService;
using VstOnlineStore.StoreBackend.Abstractions;

namespace WarehouseService.GrpcServices;

public sealed class WarehouseCatalogGrpcService(
    IStoreBackend backend) : WarehouseCatalog.WarehouseCatalogBase {

    public override async Task<FeaturedProductsResponse> GetFeaturedProducts(
        FeaturedProductsRequest request,
        ServerCallContext context) {

        var products = await backend.GetProductsAsync(context.CancellationToken);
        var response = new FeaturedProductsResponse();

        response.Products.AddRange(products.Select(product => new WarehouseProduct {
            Id = product.Id.ToString(),
            Name = product.Name,
            PriceInCents = decimal.ToInt64(product.Price * 100m),
            Image = product.Image,
            AvailableQuantity = product.AvailableQuantity,
            IsSoldOut = product.IsSoldOut
        }));

        return response;
    }

    public override async Task<SelectProductResponse> SelectProduct(
        SelectProductRequest request,
        ServerCallContext context) {

        if (!Guid.TryParse(request.ProductId, out var productId)) {
            return new SelectProductResponse {
                Success = false,
                ProductId = request.ProductId,
                Message = "Die Produkt-ID ist ungültig."
            };
        }

        var quantity = request.Quantity > 0 ? request.Quantity : 1;
        var result = await backend.ReserveProductAsync(
            productId,
            quantity,
            context.CancellationToken);

        return new SelectProductResponse {
            Success = result.Success,
            ProductId = request.ProductId,
            AvailableQuantity = result.AvailableQuantity,
            IsSoldOut = result.IsSoldOut,
            Message = result.Message
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
