using StoreBackend.Domain;

namespace StoreBackend.Application.Ports;

/// <summary>
/// Speicherport der Anwendungsschicht. Der JSON-Adapter und der spätere
/// Datenbankadapter implementieren ausschließlich diese Schnittstelle.
/// </summary>
public interface IWarehouseRepository {
    Task<IReadOnlyList<WarehouseProduct>> GetProductsAsync(
        CancellationToken cancellationToken = default);

    Task<WarehouseState> GetStateAsync(
        CancellationToken cancellationToken = default);

    Task ReplaceStateAsync(
        WarehouseState state,
        CancellationToken cancellationToken = default);
}
