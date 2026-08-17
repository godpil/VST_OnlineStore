using Grpc.Core;
using VstOnlineStore.Observability;
using AuditContracts = VstOnlineStore.Contracts.AuditService;
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
    AuditSnapshotRecorder audit,
    IStructuredLogger logger) {

    public async Task<CheckoutOutcome> CheckoutAsync(
        CheckoutRequest request,
        CancellationToken cancellationToken = default) {

        var auditState = new OrderAuditState(request.Items ?? []);
        await RecordAuditAsync(
            AuditContracts.AuditEventType.OrderStarted,
            "ShopService",
            "ANONYMOUS_USER",
            AuditContracts.AuditStatusCode.Success,
            auditState,
            cancellationToken);

        var validation = ValidateAndGroup(request.Items);
        if (!validation.Success) {
            auditState.Phase = "ORDER_VALIDATION_FAILED";
            auditState.FailureReason = validation.Message;
            auditState.Message = validation.Message;
            await RecordAuditAsync(
                AuditContracts.AuditEventType.OrderValidated,
                "ShopService",
                "ShopService",
                AuditContracts.AuditStatusCode.Failure,
                auditState,
                cancellationToken);

            return await CompleteWithFailureAsync(
                auditState,
                StatusCodes.Status400BadRequest,
                validation.Message,
                cancellationToken);
        }

        var quantities = validation.Quantities!;
        auditState.Items = quantities
            .Select(item => new OrderItemSnapshot(item.Key.ToString("D"), item.Value))
            .ToArray();
        auditState.Phase = "ORDER_VALIDATED";
        auditState.Message = "Der Warenkorb wurde validiert.";
        await RecordAuditAsync(
            AuditContracts.AuditEventType.OrderValidated,
            "ShopService",
            "ShopService",
            AuditContracts.AuditStatusCode.Success,
            auditState,
            cancellationToken);

        WarehouseContracts.FeaturedProductsResponse catalog;
        try {
            catalog = await warehouse.GetFeaturedProductsAsync(
                new WarehouseContracts.FeaturedProductsRequest(),
                cancellationToken: cancellationToken);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Unavailable) {
            return await CompleteWithFailureAsync(
                auditState,
                StatusCodes.Status503ServiceUnavailable,
                "Der WarehouseService ist nicht erreichbar.",
                cancellationToken);
        }

        var products = catalog.Products
            .Where(product => Guid.TryParse(product.Id, out _))
            .ToDictionary(product => Guid.Parse(product.Id));

        long totalInCents = 0;
        foreach (var (productId, quantity) in quantities) {
            if (!products.TryGetValue(productId, out var product)) {
                auditState.Phase = "ORDER_VALIDATION_FAILED";
                auditState.FailureReason = "Mindestens ein Produkt ist nicht mehr verfügbar.";
                auditState.Message = auditState.FailureReason;
                await RecordAuditAsync(
                    AuditContracts.AuditEventType.OrderValidated,
                    "ShopService",
                    "ShopService",
                    AuditContracts.AuditStatusCode.Failure,
                    auditState,
                    cancellationToken);

                return await CompleteWithFailureAsync(
                    auditState,
                    StatusCodes.Status400BadRequest,
                    auditState.FailureReason,
                    cancellationToken);
            }

            totalInCents = checked(totalInCents + checked(product.PriceInCents * quantity));
        }

        auditState.TotalInCents = totalInCents;

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
            auditState.Phase = "STOCK_RESERVATION_FAILED";
            auditState.FailureReason = "Der WarehouseService ist nicht erreichbar.";
            auditState.Message = auditState.FailureReason;
            await RecordAuditAsync(
                AuditContracts.AuditEventType.StockReservation,
                "WarehouseService",
                "WarehouseService",
                AuditContracts.AuditStatusCode.Failure,
                auditState,
                cancellationToken);

            return await CompleteWithFailureAsync(
                auditState,
                StatusCodes.Status503ServiceUnavailable,
                auditState.FailureReason,
                cancellationToken,
                totalInCents);
        }

        auditState.ReservationSucceeded = reservation.Success;
        auditState.Stock = reservation.Products
            .Select(product => new ProductStockSnapshot(
                product.ProductId,
                product.AvailableQuantity,
                product.IsSoldOut))
            .ToArray();
        auditState.Message = reservation.Message;
        auditState.Phase = reservation.Success
            ? "STOCK_RESERVED"
            : "STOCK_RESERVATION_FAILED";
        if (!reservation.Success) {
            auditState.FailureReason = reservation.Message;
        }
        await RecordAuditAsync(
            AuditContracts.AuditEventType.StockReservation,
            "WarehouseService",
            "WarehouseService",
            reservation.Success
                ? AuditContracts.AuditStatusCode.Success
                : AuditContracts.AuditStatusCode.Failure,
            auditState,
            cancellationToken);

        if (!reservation.Success) {
            return await CompleteWithFailureAsync(
                auditState,
                StatusCodes.Status409Conflict,
                reservation.Message,
                cancellationToken,
                totalInCents);
        }

        var reference = $"HOLZWERK-{Guid.NewGuid():N}";
        auditState.Reference = reference;
        try {
            var payment = await billing.ProcessPaymentAsync(
                new BillingContracts.PaymentRequest {
                    AmountInCents = totalInCents,
                    Currency = "EUR",
                    PaymentMethod = "demo",
                    Reference = reference
                },
                cancellationToken: cancellationToken);

            auditState.PaymentSucceeded = payment.Success;
            auditState.PaymentProvider = payment.Provider;
            auditState.TransactionId = NullIfEmpty(payment.TransactionId);
            auditState.Message = payment.Message;
            auditState.Phase = payment.Success
                ? "PAYMENT_COMPLETED"
                : "PAYMENT_FAILED";
            if (!payment.Success) {
                auditState.FailureReason = payment.Message;
            }
            await RecordAuditAsync(
                AuditContracts.AuditEventType.Payment,
                "BillingService",
                payment.Provider,
                payment.Success
                    ? AuditContracts.AuditStatusCode.Success
                    : AuditContracts.AuditStatusCode.Failure,
                auditState,
                cancellationToken);

            if (!payment.Success) {
                await ReleaseReservationAsync(
                    stockRequest,
                    auditState,
                    cancellationToken);
                return await CompleteWithFailureAsync(
                    auditState,
                    StatusCodes.Status502BadGateway,
                    payment.Message,
                    cancellationToken,
                    totalInCents);
            }

            logger.Info(
                "Checkout completed.",
                new {
                    reference,
                    provider = payment.Provider,
                    transactionId = payment.TransactionId,
                    totalInCents,
                    currency = "EUR"
                });

            auditState.Phase = "ORDER_COMPLETED";
            auditState.Message = "Zahlung und Warenreservierung wurden erfolgreich abgeschlossen.";
            await RecordAuditAsync(
                AuditContracts.AuditEventType.OrderCompleted,
                "ShopService",
                "ShopService",
                AuditContracts.AuditStatusCode.Success,
                auditState,
                cancellationToken);

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
            auditState.PaymentSucceeded = false;
            auditState.Phase = "PAYMENT_FAILED";
            auditState.FailureReason = "Der BillingService ist nicht erreichbar.";
            auditState.Message = auditState.FailureReason;
            await RecordAuditAsync(
                AuditContracts.AuditEventType.Payment,
                "BillingService",
                "BillingService",
                AuditContracts.AuditStatusCode.Failure,
                auditState,
                cancellationToken);

            await ReleaseReservationAsync(
                stockRequest,
                auditState,
                cancellationToken);
            TryLogWarning(exception, reference);
            return await CompleteWithFailureAsync(
                auditState,
                StatusCodes.Status503ServiceUnavailable,
                "Der BillingService ist nicht erreichbar. Die Reservierung wurde zurückgenommen.",
                cancellationToken,
                totalInCents);
        }
    }

    private async Task ReleaseReservationAsync(
        WarehouseContracts.CartStockRequest request,
        OrderAuditState auditState,
        CancellationToken cancellationToken) {

        auditState.Phase = "STOCK_RELEASE_REQUESTED";
        auditState.Message = "Die Kompensation der Warenreservierung wurde gestartet.";
        await RecordAuditAsync(
            AuditContracts.AuditEventType.StockRelease,
            "ShopService",
            "ShopService",
            AuditContracts.AuditStatusCode.Compensating,
            auditState,
            cancellationToken);

        try {
            var response = await warehouse.ReleaseCartAsync(
                request,
                cancellationToken: CancellationToken.None);

            auditState.ReservationSucceeded = !response.Success;
            auditState.Stock = response.Products
                .Select(product => new ProductStockSnapshot(
                    product.ProductId,
                    product.AvailableQuantity,
                    product.IsSoldOut))
                .ToArray();
            auditState.Phase = response.Success
                ? "STOCK_RELEASED"
                : "STOCK_RELEASE_FAILED";
            auditState.Message = response.Message;
            await RecordAuditAsync(
                AuditContracts.AuditEventType.StockRelease,
                "WarehouseService",
                "WarehouseService",
                response.Success
                    ? AuditContracts.AuditStatusCode.Compensated
                    : AuditContracts.AuditStatusCode.Failure,
                auditState,
                CancellationToken.None);

            if (!response.Success) {
                TryLogError(
                    null,
                    $"Reservierung konnte nicht zurückgenommen werden: {response.Message}");
            }
        }
        catch (RpcException exception) {
            auditState.Phase = "STOCK_RELEASE_FAILED";
            auditState.Message = "Reservierung konnte nach Abrechnungsfehler nicht zurückgenommen werden.";
            await RecordAuditAsync(
                AuditContracts.AuditEventType.StockRelease,
                "WarehouseService",
                "WarehouseService",
                AuditContracts.AuditStatusCode.Failure,
                auditState,
                CancellationToken.None);
            TryLogError(exception, auditState.Message);
        }
    }

    private async Task<CheckoutOutcome> CompleteWithFailureAsync(
        OrderAuditState auditState,
        int statusCode,
        string message,
        CancellationToken cancellationToken,
        long totalInCents = 0) {

        auditState.Phase = "ORDER_FAILED";
        auditState.FailureReason ??= message;
        auditState.Message = message;
        await RecordAuditAsync(
            AuditContracts.AuditEventType.OrderCompleted,
            "ShopService",
            "ShopService",
            AuditContracts.AuditStatusCode.Failure,
            auditState,
            cancellationToken);

        return Failed(statusCode, message, totalInCents);
    }

    private Task RecordAuditAsync(
        AuditContracts.AuditEventType eventType,
        string responsibleService,
        string actor,
        AuditContracts.AuditStatusCode statusCode,
        OrderAuditState auditState,
        CancellationToken cancellationToken) =>
        audit.RecordAsync(
            eventType,
            responsibleService,
            auditState,
            actor,
            statusCode,
            cancellationToken);

    private void TryLogWarning(Exception exception, string reference) {
        try {
            logger.Warn(
                "Billing failed.",
                new {
                    reference,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);
        }
        catch (Exception) {
            // Eine nicht verfügbare Log-Senke darf das fachliche Rollback
            // und die HTTP-Antwort nicht verhindern.
        }
    }

    private void TryLogError(Exception? exception, string message) {
        try {
            logger.Error(
                message,
                exception is null
                    ? null
                    : new {
                        exceptionType = exception.GetType().FullName,
                        exceptionMessage = exception.Message
                    },
                exception);
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

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record ValidationResult(
        bool Success,
        string Message,
        IReadOnlyDictionary<Guid, int>? Quantities);

    private sealed class OrderAuditState(
        IReadOnlyList<CheckoutItemRequest> requestedItems) {

        public string Phase { get; set; } = "ORDER_STARTED";
        public IReadOnlyList<OrderItemSnapshot> Items { get; set; } = requestedItems
            .Select(item => new OrderItemSnapshot(item.ProductId, item.Quantity))
            .ToArray();
        public long? TotalInCents { get; set; }
        public string Currency { get; } = "EUR";
        public string? Reference { get; set; }
        public bool? ReservationSucceeded { get; set; }
        public IReadOnlyList<ProductStockSnapshot> Stock { get; set; } = [];
        public bool? PaymentSucceeded { get; set; }
        public string? PaymentProvider { get; set; }
        public string? TransactionId { get; set; }
        public string? Message { get; set; }
        public string? FailureReason { get; set; }
    }

    private sealed record OrderItemSnapshot(
        string ProductId,
        int Quantity);

    private sealed record ProductStockSnapshot(
        string ProductId,
        int AvailableQuantity,
        bool IsSoldOut);
}
