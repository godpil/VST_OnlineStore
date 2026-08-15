namespace VstOnlineStore.StoreBackend.Domain;

public sealed record ProductReservationResult(
    bool Success,
    Guid ProductId,
    int AvailableQuantity,
    bool IsSoldOut,
    string Message);
