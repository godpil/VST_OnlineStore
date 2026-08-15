namespace StoreBackend.Domain;

public sealed record ProductStock(
    Guid ProductId,
    int AvailableQuantity,
    bool IsSoldOut);
