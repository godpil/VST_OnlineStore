using Grpc.Core;
using BillingContracts = VstOnlineStore.Contracts.BillingService;
using WarehouseContracts = VstOnlineStore.Contracts.WarehouseService;

namespace ShopService.Checkout;

/// <summary>
/// Koordiniert einen Kauf über WarehouseService und BillingService.
/// Erst wird der gesamte Warenkorb reserviert, danach wird bezahlt.
/// Scheitert die Zahlung, wird die Reservierung zurückgenommen.
/// </summary>
public sealed class CheckoutOrchestrator(
    WarehouseContracts.WarehouseCatalog.WarehouseCatalogClient warehouse,
    BillingContracts.BillingOperations.BillingOperationsClient billing,
    ILogger<CheckoutOrchestrator> logger) {

    public async Task<CheckoutOutcome> CheckoutAsync(
        CheckoutRequest request,
        CancellationToken cancellationToken = default) {

        var validation = ValidateAndGroup(request.Items);
        if (!validation.Success) {
            return Failed(StatusCodes.Status400BadRequest, validation.Message);
        }

        var quantities = validation.Quantities!;
        WarehouseContracts.FeaturedProductsResponse catalog;
        try {
            catalog = await warehouse.GetFeaturedProductsAsync(
                new WarehouseContracts.FeaturedProductsRequest(),
                cancellationToken: cancellationToken);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Unavailable) {
            return Failed(StatusCodes.Status503ServiceUnavailable, "Der WarehouseService ist nicht erreichbar.");
        }

        var products = catalog.Products
            .Where(product => Guid.TryParse(product.Id, out _))
            .ToDictionary(product => Guid.Parse(product.Id));

        long totalInCents = 0;
        foreach (var (productId, quantity) in quantities) {
            if (!products.TryGetValue(productId, out var product)) {
                return Failed(StatusCodes.Status400BadRequest, "Mindestens ein Produkt ist nicht mehr verfügbar.");
            }

            totalInCents = checked(totalInCents + checked(product.PriceInCents * quantity));
        }

        var stockRequest = new WarehouseContracts.CartStockRequest();
        stockRequest.Items.AddRange(quantities.Select(item =>
            new WarehouseContracts.CartProductQuantity {
                ProductId = item.Key.ToString(),
                Quantity = item.Value
            }));

        WarehouseContracts.CartStockResponse reservation;
        try {
            reservation = await warehouse.ReserveCartAsync(
                stockRequest,
                cancellationToken: cancellationToken);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Unavailable) {
            return Failed(StatusCodes.Status503ServiceUnavailable, "Der WarehouseService ist nicht erreichbar.");
        }

        if (!reservation.Success) {
            return Failed(StatusCodes.Status409Conflict, reservation.Message);
        }

        var reference = $"HOLZWERK-{Guid.NewGuid():N}";
        try {
            var payment = await billing.ProcessPaymentAsync(
                new BillingContracts.PaymentRequest {
                    AmountInCents = totalInCents,
                    Currency = "EUR",
                    PaymentMethod = "demo",
                    Reference = reference
                },
                cancellationToken: cancellationToken);

            if (!payment.Success) {
                await ReleaseReservationAsync(stockRequest);
                return Failed(
                    StatusCodes.Status502BadGateway,
                    payment.Message,
                    totalInCents);
            }

            logger.LogInformation(
                "Kauf {Reference} über {Provider} abgeschlossen. Transaktion: {TransactionId}",
                reference,
                payment.Provider,
                payment.TransactionId);

            return new CheckoutOutcome(
                StatusCodes.Status200OK,
                new CheckoutResponse(
                    true,
                    "Vielen Dank! Die Zahlung war erfolgreich und die Ware ist reserviert.",
                    totalInCents / 100m,
                    "EUR",
                    payment.TransactionId,
                    payment.Provider));
        }
        catch (RpcException exception) {
            await ReleaseReservationAsync(stockRequest);
            TryLogWarning(exception, reference);
            return Failed(
                StatusCodes.Status503ServiceUnavailable,
                "Der BillingService ist nicht erreichbar. Die Reservierung wurde zurückgenommen.",
                totalInCents);
        }
    }

    private async Task ReleaseReservationAsync(
        WarehouseContracts.CartStockRequest request) {

        try {
            var response = await warehouse.ReleaseCartAsync(
                request,
                cancellationToken: CancellationToken.None);

            if (!response.Success) {
                TryLogError(
                    null,
                    $"Reservierung konnte nicht zurückgenommen werden: {response.Message}");
            }
        }
        catch (RpcException exception) {
            TryLogError(
                exception,
                "Reservierung konnte nach Abrechnungsfehler nicht zurückgenommen werden.");
        }
    }

    private void TryLogWarning(Exception exception, string reference) {
        try {
            logger.LogWarning(exception, "Abrechnung für {Reference} fehlgeschlagen.", reference);
        }
        catch (Exception) {
            // Eine nicht verfügbare Log-Senke darf das fachliche Rollback
            // und die HTTP-Antwort nicht verhindern.
        }
    }

    private void TryLogError(Exception? exception, string message) {
        try {
            logger.LogError(exception, "{Message}", message);
        }
        catch (Exception) {
            // Siehe TryLogWarning.
        }
    }

    private static ValidationResult ValidateAndGroup(
        IReadOnlyList<CheckoutItemRequest>? items) {

        if (items is null || items.Count == 0) {
            return new ValidationResult(false, "Der Warenkorb ist leer.", null);
        }

        var parsedItems = new List<(Guid ProductId, int Quantity)>();
        foreach (var item in items) {
            if (!Guid.TryParse(item.ProductId, out var productId) || item.Quantity <= 0) {
                return new ValidationResult(
                    false,
                    "Alle Produkt-IDs und Mengen müssen gültig sein.",
                    null);
            }

            parsedItems.Add((productId, item.Quantity));
        }

        var quantities = parsedItems
            .GroupBy(item => item.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));

        return new ValidationResult(true, string.Empty, quantities);
    }

    private static CheckoutOutcome Failed(
        int statusCode,
        string message,
        long totalInCents = 0) =>
        new(
            statusCode,
            new CheckoutResponse(
                false,
                message,
                totalInCents / 100m,
                "EUR",
                null,
                null));

    private sealed record ValidationResult(
        bool Success,
        string Message,
        IReadOnlyDictionary<Guid, int>? Quantities);
}
