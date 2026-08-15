namespace VstOnlineStore.StoreBackend.Domain;

/// <summary>
/// Produkt mit seinem aktuell verfügbaren Lagerbestand.
/// </summary>
public sealed record WarehouseProduct(
    Guid Id,
    string Name,
    decimal Price,
    string Image,
    int AvailableQuantity) {

    public bool IsSoldOut => AvailableQuantity == 0;
}
