namespace StoreBackend.Domain;

public sealed record StockChangeResult(
    bool Success,
    IReadOnlyList<ProductStock> Products,
    string Message);
