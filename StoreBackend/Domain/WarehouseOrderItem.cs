namespace StoreBackend.Domain;

public sealed record WarehouseOrderItem(Guid ProductId, int Quantity);
