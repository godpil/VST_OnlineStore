using Grpc.Core;
using StoreBackend.Application;
using StoreBackend.Contracts;

namespace StoreBackend.Services;

/// <summary>
/// Interner gRPC-Transportadapter. Er übersetzt nur zwischen Protobuf und
/// Anwendungsschicht; Geschäfts- und Speicherlogik liegen nicht hier.
/// </summary>
public sealed class WarehouseStorageGrpcService(
    WarehouseApplicationService warehouse,
    ILogger<WarehouseStorageGrpcService> logger) : WarehouseStorage.WarehouseStorageBase {

    public override async Task<BackendProductsResponse> GetProducts(
        BackendProductsRequest request,
        ServerCallContext context) {

        var products = await warehouse.GetProductsAsync(context.CancellationToken);
        var response = new BackendProductsResponse();

        response.Products.AddRange(products.Select(product => new StoredProduct {
            Id = product.Id.ToString(),
            Name = product.Name,
            PriceInCents = decimal.ToInt64(product.Price * 100m),
            Image = product.Image,
            AvailableQuantity = product.AvailableQuantity,
            IsSoldOut = product.IsSoldOut
        }));

        return response;
    }

    public override async Task<BackendReserveProductResponse> ReserveProduct(
        BackendReserveProductRequest request,
        ServerCallContext context) {

        if (!Guid.TryParse(request.ProductId, out var productId)) {
            return new BackendReserveProductResponse {
                Success = false,
                ProductId = request.ProductId,
                Message = "Die Produkt-ID ist ungültig."
            };
        }

        if (request.Quantity <= 0) {
            return new BackendReserveProductResponse {
                Success = false,
                ProductId = request.ProductId,
                Message = "Die Reservierungsmenge muss größer als null sein."
            };
        }

        var result = await warehouse.ReserveProductAsync(
            productId,
            request.Quantity,
            context.CancellationToken);

        logger.LogInformation(
            "Reservierung von {Quantity} × {ProductId}. Ergebnis: {Success}",
            request.Quantity,
            request.ProductId,
            result.Success);

        return new BackendReserveProductResponse {
            Success = result.Success,
            ProductId = request.ProductId,
            AvailableQuantity = result.AvailableQuantity,
            IsSoldOut = result.IsSoldOut,
            Message = result.Message
        };
    }
}
