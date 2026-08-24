namespace StoreBackend.Domain;

public enum WarehouseReservationStatus {
    ACTIVE,
    COMMITTED,
    RELEASED
}

/// <summary>
/// Persistenter Lebenszyklus einer Warenreservierung. Die Bestell-ID dient als
/// fachlich eindeutige Reservierungs-ID und macht Reserve, Commit und Release
/// bei wiederholten Nachrichten idempotent.
/// </summary>
public sealed record WarehouseReservation(
    Guid ReservationId,
    IReadOnlyList<WarehouseOrderItem> Items,
    WarehouseReservationStatus Status,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc = null);

public sealed record WarehouseState(
    IReadOnlyList<WarehouseProduct> Products,
    IReadOnlyList<WarehouseReservation> Reservations);
