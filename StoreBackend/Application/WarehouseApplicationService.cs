using StoreBackend.Application.Ports;
using StoreBackend.Domain;

namespace StoreBackend.Application;

/// <summary>
/// Enthält die fachlichen Lageranwendungsfälle unabhängig von gRPC und
/// der konkreten Speicherung.
/// </summary>
public sealed class WarehouseApplicationService(
    IWarehouseRepository repository) {

    private readonly SemaphoreSlim _reservationLock = new(1, 1);

    public Task<IReadOnlyList<WarehouseProduct>> GetProductsAsync(
        CancellationToken cancellationToken = default) =>
        repository.GetProductsAsync(cancellationToken);

    public async Task<ProductReservationResult> ReserveProductAsync(
        Guid productId,
        int quantity,
        CancellationToken cancellationToken = default) {

        if (quantity <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(quantity),
                "Die Reservierungsmenge muss größer als null sein.");
        }

        await _reservationLock.WaitAsync(cancellationToken);
        try {
            var product = await repository.GetProductAsync(
                productId,
                cancellationToken);

            if (product is null) {
                return new ProductReservationResult(
                    false,
                    productId,
                    0,
                    false,
                    "Das Produkt wurde nicht gefunden.");
            }

            if (product.AvailableQuantity < quantity) {
                return new ProductReservationResult(
                    false,
                    product.Id,
                    product.AvailableQuantity,
                    product.IsSoldOut,
                    product.IsSoldOut
                        ? "Das Produkt ist ausverkauft."
                        : $"Es sind nur noch {product.AvailableQuantity} Stück verfügbar.");
            }

            var updatedProduct = product with {
                AvailableQuantity = product.AvailableQuantity - quantity
            };

            await repository.SaveProductAsync(updatedProduct, cancellationToken);

            return new ProductReservationResult(
                true,
                updatedProduct.Id,
                updatedProduct.AvailableQuantity,
                updatedProduct.IsSoldOut,
                "Das Produkt wurde reserviert.");
        }
        finally {
            _reservationLock.Release();
        }
    }
}
