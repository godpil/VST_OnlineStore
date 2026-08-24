using StoreBackend.Application.Ports;
using StoreBackend.Domain;

namespace StoreBackend.Application;

/// <summary>
/// Enthält die fachlichen Lageranwendungsfälle unabhängig von gRPC und
/// der konkreten Speicherung.
/// </summary>
public sealed class WarehouseApplicationService(
    IWarehouseRepository repository) {

    private readonly SemaphoreSlim _stockLock = new(1, 1);

    public Task<IReadOnlyList<WarehouseProduct>> GetProductsAsync(
        CancellationToken cancellationToken = default) =>
        repository.GetProductsAsync(cancellationToken);

    public Task<StockChangeResult> ReserveProductsAsync(
        Guid reservationId,
        IReadOnlyCollection<WarehouseOrderItem> items,
        CancellationToken cancellationToken = default) =>
        ChangeReservationAsync(
            reservationId,
            items,
            ReservationOperation.Reserve,
            cancellationToken);

    public Task<StockChangeResult> CommitProductsAsync(
        Guid reservationId,
        IReadOnlyCollection<WarehouseOrderItem> items,
        CancellationToken cancellationToken = default) =>
        ChangeReservationAsync(
            reservationId,
            items,
            ReservationOperation.Commit,
            cancellationToken);

    public Task<StockChangeResult> ReleaseProductsAsync(
        Guid reservationId,
        IReadOnlyCollection<WarehouseOrderItem> items,
        CancellationToken cancellationToken = default) =>
        ChangeReservationAsync(
            reservationId,
            items,
            ReservationOperation.Release,
            cancellationToken);

    private async Task<StockChangeResult> ChangeReservationAsync(
        Guid reservationId,
        IReadOnlyCollection<WarehouseOrderItem> items,
        ReservationOperation operation,
        CancellationToken cancellationToken) {

        if (reservationId == Guid.Empty) {
            return Failed("Die Reservierungs-ID ist ungültig.");
        }

        if (items.Count == 0) {
            return Failed("Der Warenkorb ist leer.");
        }

        if (items.Any(item => item.ProductId == Guid.Empty || item.Quantity <= 0)) {
            return Failed("Alle Produkt-IDs und Mengen müssen gültig sein.");
        }

        var quantities = items
            .GroupBy(item => item.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
        var normalizedItems = quantities
            .OrderBy(item => item.Key)
            .Select(item => new WarehouseOrderItem(item.Key, item.Value))
            .ToArray();

        await _stockLock.WaitAsync(cancellationToken);
        try {
            var state = await repository.GetStateAsync(cancellationToken);
            var productsById = state.Products.ToDictionary(product => product.Id);
            var reservationsById = state.Reservations.ToDictionary(
                reservation => reservation.ReservationId);

            return operation switch {
                ReservationOperation.Reserve => await ReserveAsync(
                    reservationId,
                    normalizedItems,
                    quantities,
                    productsById,
                    reservationsById,
                    cancellationToken),
                ReservationOperation.Commit => await CompleteAsync(
                    reservationId,
                    normalizedItems,
                    productsById,
                    reservationsById,
                    commit: true,
                    cancellationToken),
                ReservationOperation.Release => await CompleteAsync(
                    reservationId,
                    normalizedItems,
                    productsById,
                    reservationsById,
                    commit: false,
                    cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            };
        }
        finally {
            _stockLock.Release();
        }
    }

    private async Task<StockChangeResult> ReserveAsync(
        Guid reservationId,
        IReadOnlyList<WarehouseOrderItem> normalizedItems,
        IReadOnlyDictionary<Guid, int> quantities,
        Dictionary<Guid, WarehouseProduct> productsById,
        Dictionary<Guid, WarehouseReservation> reservationsById,
        CancellationToken cancellationToken) {

        if (reservationsById.TryGetValue(reservationId, out var existing)) {
            if (!HasSameItems(existing.Items, normalizedItems)) {
                return Failed(
                    "Die Reservierungs-ID wurde bereits für einen anderen Warenkorb verwendet.",
                    productsById,
                    quantities.Keys);
            }

            return existing.Status switch {
                WarehouseReservationStatus.ACTIVE => Succeeded(
                    "Der Warenkorb war bereits vollständig reserviert.",
                    productsById,
                    quantities.Keys),
                WarehouseReservationStatus.COMMITTED => Succeeded(
                    "Der Warenkorb wurde bereits endgültig ausgebucht.",
                    productsById,
                    quantities.Keys),
                WarehouseReservationStatus.RELEASED => Failed(
                    "Eine bereits freigegebene Reservierung kann nicht erneut verwendet werden.",
                    productsById,
                    quantities.Keys),
                _ => throw new ArgumentOutOfRangeException(nameof(existing.Status))
            };
        }

        foreach (var (productId, quantity) in quantities) {
            if (!productsById.TryGetValue(productId, out var product)) {
                return Failed("Mindestens ein Produkt wurde nicht gefunden.");
            }

            if (product.AvailableQuantity < quantity) {
                var message = product.IsSoldOut
                    ? $"{product.Name} ist ausverkauft."
                    : $"Von {product.Name} sind nur noch {product.AvailableQuantity} Stück verfügbar.";
                return Failed(message, productsById, quantities.Keys);
            }
        }

        foreach (var (productId, quantity) in quantities) {
            var product = productsById[productId];
            productsById[productId] = product with {
                AvailableQuantity = product.AvailableQuantity - quantity
            };
        }

        reservationsById.Add(
            reservationId,
            new WarehouseReservation(
                reservationId,
                normalizedItems,
                WarehouseReservationStatus.ACTIVE,
                DateTime.UtcNow));

        await PersistAsync(productsById, reservationsById, cancellationToken);
        return Succeeded(
            "Der Warenkorb wurde vollständig reserviert.",
            productsById,
            quantities.Keys);
    }

    private async Task<StockChangeResult> CompleteAsync(
        Guid reservationId,
        IReadOnlyList<WarehouseOrderItem> normalizedItems,
        Dictionary<Guid, WarehouseProduct> productsById,
        Dictionary<Guid, WarehouseReservation> reservationsById,
        bool commit,
        CancellationToken cancellationToken) {

        if (!reservationsById.TryGetValue(reservationId, out var reservation)) {
            return Failed("Die Reservierung wurde nicht gefunden.");
        }

        var productIds = reservation.Items.Select(item => item.ProductId).ToArray();
        if (!HasSameItems(reservation.Items, normalizedItems)) {
            return Failed(
                "Der Warenkorb stimmt nicht mit der gespeicherten Reservierung überein.",
                productsById,
                productIds);
        }

        if (commit) {
            if (reservation.Status == WarehouseReservationStatus.COMMITTED) {
                return Succeeded(
                    "Die Reservierung wurde bereits endgültig ausgebucht.",
                    productsById,
                    productIds);
            }

            if (reservation.Status == WarehouseReservationStatus.RELEASED) {
                return Failed(
                    "Eine freigegebene Reservierung kann nicht ausgebucht werden.",
                    productsById,
                    productIds);
            }

            reservationsById[reservationId] = reservation with {
                Status = WarehouseReservationStatus.COMMITTED,
                CompletedAtUtc = DateTime.UtcNow
            };
            await PersistAsync(productsById, reservationsById, cancellationToken);
            return Succeeded(
                "Die reservierten Artikel wurden endgültig ausgebucht.",
                productsById,
                productIds);
        }

        if (reservation.Status == WarehouseReservationStatus.RELEASED) {
            return Succeeded(
                "Die Reservierung wurde bereits zurückgenommen.",
                productsById,
                productIds);
        }

        if (reservation.Status == WarehouseReservationStatus.COMMITTED) {
            return Failed(
                "Eine bereits ausgebuchte Reservierung kann nicht freigegeben werden.",
                productsById,
                productIds);
        }

        foreach (var item in reservation.Items) {
            var product = productsById[item.ProductId];
            productsById[item.ProductId] = product with {
                AvailableQuantity = checked(product.AvailableQuantity + item.Quantity)
            };
        }

        reservationsById[reservationId] = reservation with {
            Status = WarehouseReservationStatus.RELEASED,
            CompletedAtUtc = DateTime.UtcNow
        };
        await PersistAsync(productsById, reservationsById, cancellationToken);
        return Succeeded(
            "Die Reservierung wurde vollständig zurückgenommen.",
            productsById,
            productIds);
    }

    private Task PersistAsync(
        IReadOnlyDictionary<Guid, WarehouseProduct> products,
        IReadOnlyDictionary<Guid, WarehouseReservation> reservations,
        CancellationToken cancellationToken) =>
        repository.ReplaceStateAsync(
            new WarehouseState(
                products.Values.ToArray(),
                reservations.Values.ToArray()),
            cancellationToken);

    private static bool HasSameItems(
        IEnumerable<WarehouseOrderItem> left,
        IEnumerable<WarehouseOrderItem> right) =>
        left.OrderBy(item => item.ProductId).SequenceEqual(
            right.OrderBy(item => item.ProductId));

    private static StockChangeResult Succeeded(
        string message,
        IReadOnlyDictionary<Guid, WarehouseProduct> products,
        IEnumerable<Guid> productIds) =>
        new(true, ToStocks(products, productIds), message);

    private static StockChangeResult Failed(
        string message,
        IReadOnlyDictionary<Guid, WarehouseProduct>? products = null,
        IEnumerable<Guid>? productIds = null) =>
        new(false, ToStocks(products, productIds), message);

    private static IReadOnlyList<ProductStock> ToStocks(
        IReadOnlyDictionary<Guid, WarehouseProduct>? products,
        IEnumerable<Guid>? productIds) =>
        products is null || productIds is null
            ? Array.Empty<ProductStock>()
            : productIds
                .Where(products.ContainsKey)
                .Select(productId => ToStock(products[productId]))
                .ToArray();

    private static ProductStock ToStock(WarehouseProduct product) =>
        new(
            product.Id,
            product.Name,
            product.Price,
            product.AvailableQuantity,
            product.IsSoldOut);

    private enum ReservationOperation {
        Reserve,
        Commit,
        Release
    }
}
