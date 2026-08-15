using StoreBackend.Contracts;
using VstOnlineStore.StoreBackend.Abstractions;
using VstOnlineStore.StoreBackend.Domain;

namespace WarehouseService.Backend;

/// <summary>
/// Implementiert den fachlichen StoreBackend-Port über den internen gRPC-Vertrag.
/// Der restliche WarehouseService kennt dadurch keine Transportdetails des Backends.
/// </summary>
public sealed class GrpcStoreBackendAdapter(
    WarehouseStorage.WarehouseStorageClient client) : IStoreBackend {

    public async Task<IReadOnlyList<WarehouseProduct>> GetProductsAsync(
        CancellationToken cancellationToken = default) {

        var response = await client.GetProductsAsync(
            new BackendProductsRequest(),
            cancellationToken: cancellationToken);

        return response.Products.Select(MapProduct).ToArray();
    }

    public async Task<WarehouseProduct?> GetProductAsync(
        Guid productId,
        CancellationToken cancellationToken = default) {

        var products = await GetProductsAsync(cancellationToken);
        return products.FirstOrDefault(product => product.Id == productId);
    }

    public async Task<ProductReservationResult> ReserveProductAsync(
        Guid productId,
        int quantity,
        CancellationToken cancellationToken = default) {

        var response = await client.ReserveProductAsync(
            new BackendReserveProductRequest {
                ProductId = productId.ToString(),
                Quantity = quantity
            },
            cancellationToken: cancellationToken);

        return new ProductReservationResult(
            response.Success,
            productId,
            response.AvailableQuantity,
            response.IsSoldOut,
            response.Message);
    }

    private static WarehouseProduct MapProduct(StoredProduct product) => new(
        Guid.Parse(product.Id),
        product.Name,
        product.PriceInCents / 100m,
        product.Image,
        product.AvailableQuantity);
}
