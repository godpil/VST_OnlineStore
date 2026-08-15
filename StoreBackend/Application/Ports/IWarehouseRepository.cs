using StoreBackend.Domain;

namespace StoreBackend.Application.Ports;

/// <summary>
/// Speicherport der Anwendungsschicht. Der JSON-Adapter und der spätere
/// Datenbankadapter implementieren ausschließlich diese Schnittstelle.
/// </summary>
public interface IWarehouseRepository {
    Task<IReadOnlyList<WarehouseProduct>> GetProductsAsync(
        CancellationToken cancellationToken = default);

    Task<WarehouseProduct?> GetProductAsync(
        Guid productId,
        CancellationToken cancellationToken = default);

    Task SaveProductAsync(
        WarehouseProduct product,
        CancellationToken cancellationToken = default);
}
