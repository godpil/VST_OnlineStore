namespace StoreBackend.Domain;

public sealed record ProductStock(
    Guid ProductId,
    string Name,
    decimal Price,
    int AvailableQuantity,
    bool IsSoldOut);
