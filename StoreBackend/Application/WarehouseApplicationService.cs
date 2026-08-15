using StoreBackend.Application.Ports;
using StoreBackend.Domain;

namespace StoreBackend.Application;

/// <summary>
/// Enthält die fachlichen Lageranwendungsfälle unabhängig von gRPC und
/// der konkreten Speicherung.
/// </summary>
public sealed class WarehouseApplicationService(
    IWarehouseRepository repository) {

    private readonly SemaphoreSlim _stockLock = new(1, 1);

    public Task<IReadOnlyList<WarehouseProduct>> GetProductsAsync(
        CancellationToken cancellationToken = default) =>
        repository.GetProductsAsync(cancellationToken);

    public Task<StockChangeResult> ReserveProductsAsync(
        IReadOnlyCollection<WarehouseOrderItem> items,
        CancellationToken cancellationToken = default) =>
        ChangeStockAsync(items, reserve: true, cancellationToken);

    public Task<StockChangeResult> ReleaseProductsAsync(
        IReadOnlyCollection<WarehouseOrderItem> items,
        CancellationToken cancellationToken = default) =>
        ChangeStockAsync(items, reserve: false, cancellationToken);

    private async Task<StockChangeResult> ChangeStockAsync(
        IReadOnlyCollection<WarehouseOrderItem> items,
        bool reserve,
        CancellationToken cancellationToken) {

        if (items.Count == 0) {
            return Failed("Der Warenkorb ist leer.");
        }

        if (items.Any(item => item.ProductId == Guid.Empty || item.Quantity <= 0)) {
            return Failed("Alle Produkt-IDs und Mengen müssen gültig sein.");
        }

        var quantities = items
            .GroupBy(item => item.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));

        await _stockLock.WaitAsync(cancellationToken);
        try {
            var products = (await repository.GetProductsAsync(cancellationToken)).ToArray();
            var productsById = products.ToDictionary(product => product.Id);

            foreach (var (productId, quantity) in quantities) {
                if (!productsById.TryGetValue(productId, out var product)) {
                    return Failed("Mindestens ein Produkt wurde nicht gefunden.");
                }

                if (reserve && product.AvailableQuantity < quantity) {
                    var message = product.IsSoldOut
                        ? $"{product.Name} ist ausverkauft."
                        : $"Von {product.Name} sind nur noch {product.AvailableQuantity} Stück verfügbar.";
                    return Failed(message, productsById, quantities.Keys);
                }
            }

            foreach (var (productId, quantity) in quantities) {
                var product = productsById[productId];
                productsById[productId] = product with {
                    AvailableQuantity = reserve
                        ? product.AvailableQuantity - quantity
                        : checked(product.AvailableQuantity + quantity)
                };
            }

            await repository.ReplaceProductsAsync(
                productsById.Values.ToArray(),
                cancellationToken);

            var changedProducts = quantities.Keys
                .Select(productId => ToStock(productsById[productId]))
                .ToArray();

            return new StockChangeResult(
                true,
                changedProducts,
                reserve
                    ? "Der Warenkorb wurde vollständig reserviert."
                    : "Die Reservierung wurde vollständig zurückgenommen.");
        }
        finally {
            _stockLock.Release();
        }
    }

    private static StockChangeResult Failed(
        string message,
        IReadOnlyDictionary<Guid, WarehouseProduct>? products = null,
        IEnumerable<Guid>? productIds = null) {

        var stocks = products is null || productIds is null
            ? Array.Empty<ProductStock>()
            : productIds
                .Where(products.ContainsKey)
                .Select(productId => ToStock(products[productId]))
                .ToArray();

        return new StockChangeResult(false, stocks, message);
    }

    private static ProductStock ToStock(WarehouseProduct product) =>
        new(product.Id, product.AvailableQuantity, product.IsSoldOut);
}
