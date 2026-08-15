using VstOnlineStore.StoreBackend.Domain;

namespace VstOnlineStore.StoreBackend.Abstractions;

/// <summary>
/// Fachliche Schnittstelle auf den Lagerbestand des OnlineStores.
/// Implementierungen können die Daten im Speicher, in einer Datei oder
/// später in einer Datenbank halten.
/// </summary>
public interface IStoreBackend {
    Task<IReadOnlyList<WarehouseProduct>> GetProductsAsync(
        CancellationToken cancellationToken = default);

    Task<WarehouseProduct?> GetProductAsync(
        Guid productId,
        CancellationToken cancellationToken = default);

    Task<ProductReservationResult> ReserveProductAsync(
        Guid productId,
        int quantity,
        CancellationToken cancellationToken = default);
}
