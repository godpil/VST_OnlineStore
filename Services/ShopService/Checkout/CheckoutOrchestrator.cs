using System.Net.Mail;
using Grpc.Core;
using VstOnlineStore.Observability;
using AuditEventType = VstOnlineStore.Observability.Auditing.AuditEventType;
using AuditStatusCode = VstOnlineStore.Observability.Auditing.AuditStatusCode;
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

        var requestedPaymentProvider = request.PaymentProvider?.Trim();
        var customerEmail = request.CustomerEmail?.Trim();
        var auditState = new OrderAuditState(
            request.Items ?? [],
            requestedPaymentProvider,
            GetRecipientDomain(customerEmail));
        await RecordAuditAsync(
            AuditEventType.ORDER_STARTED,
            "ShopService",
            "ANONYMOUS_USER",
            AuditStatusCode.SUCCESS,
            auditState,
            cancellationToken);

        var validation = ValidateAndGroup(
            request.Items,
            requestedPaymentProvider,
            customerEmail);
        if (!validation.Success) {
            auditState.Phase = "ORDER_VALIDATION_FAILED";
            auditState.FailureReason = validation.Message;
            auditState.Message = validation.Message;
            await RecordAuditAsync(
                AuditEventType.ORDER_VALIDATED,
                "ShopService",
                "ShopService",
                AuditStatusCode.FAILURE,
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
            AuditEventType.ORDER_VALIDATED,
            "ShopService",
            "ShopService",
            AuditStatusCode.SUCCESS,
            auditState,
            cancellationToken);

        BillingContracts.PaymentProviderInfo? selectedPaymentProvider;
        try {
            var availableProviders = await billing.ListPaymentProvidersAsync(
                new BillingContracts.PaymentProvidersRequest(),
                cancellationToken: cancellationToken);
            selectedPaymentProvider = availableProviders.Providers.FirstOrDefault(provider =>
                provider.Key.Equals(
                    requestedPaymentProvider,
                    StringComparison.OrdinalIgnoreCase));

            if (selectedPaymentProvider is null) {
                auditState.Phase = "PAYMENT_PROVIDER_REJECTED";
                auditState.FailureReason = "Der ausgewählte Zahlungsanbieter ist nicht verfügbar.";
                auditState.Message = auditState.FailureReason;
                await RecordAuditAsync(
                    AuditEventType.PAYMENT,
                    "ShopService",
                    "BillingService",
                    AuditStatusCode.FAILURE,
                    auditState,
                    cancellationToken);
                logger.Warn(
                    "Checkout rejected an unknown payment provider.",
                    new {
                        requestedPaymentProvider
                    });
                return await CompleteWithFailureAsync(
                    auditState,
                    StatusCodes.Status400BadRequest,
                    auditState.FailureReason,
                    cancellationToken);
            }
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Unavailable) {
            auditState.Phase = "PAYMENT_PROVIDER_LOOKUP_FAILED";
            auditState.FailureReason = "Der BillingService ist nicht erreichbar.";
            auditState.Message = auditState.FailureReason;
            await RecordAuditAsync(
                AuditEventType.PAYMENT,
                "ShopService",
                "BillingService",
                AuditStatusCode.FAILURE,
                auditState,
                cancellationToken);
            TryLogWarning(exception, requestedPaymentProvider ?? string.Empty);
            return await CompleteWithFailureAsync(
                auditState,
                StatusCodes.Status503ServiceUnavailable,
                auditState.FailureReason,
                cancellationToken);
        }

        auditState.PaymentProviderKey = selectedPaymentProvider.Key;
        auditState.PaymentProvider = selectedPaymentProvider.Name;
        auditState.Phase = "PAYMENT_PROVIDER_SELECTED";
        auditState.Message = $"{selectedPaymentProvider.Name} wurde als Zahlungsanbieter ausgewählt.";
        await RecordAuditAsync(
            AuditEventType.PAYMENT,
            "ShopService",
            selectedPaymentProvider.Name,
            AuditStatusCode.SUCCESS,
            auditState,
            cancellationToken);
        logger.Info(
            "Payment provider selected for checkout.",
            new {
                providerKey = selectedPaymentProvider.Key,
                providerName = selectedPaymentProvider.Name,
                selectedPaymentProvider.IsTestMode
            });

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
                    AuditEventType.ORDER_VALIDATED,
                    "ShopService",
                    "ShopService",
                    AuditStatusCode.FAILURE,
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
                AuditEventType.STOCK_RESERVATION,
                "ShopService",
                "ShopService",
                AuditStatusCode.FAILURE,
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
            AuditEventType.STOCK_RESERVATION,
            "ShopService",
            "WarehouseService",
            reservation.Success
                ? AuditStatusCode.SUCCESS
                : AuditStatusCode.FAILURE,
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
            var paymentRequest = new BillingContracts.PaymentRequest {
                AmountInCents = totalInCents,
                Currency = "EUR",
                PaymentMethod = "test",
                Reference = reference,
                PaymentProvider = selectedPaymentProvider.Key,
                CustomerEmail = customerEmail
            };
            paymentRequest.InvoiceItems.AddRange(quantities.Select(item => {
                var product = products[item.Key];
                return new BillingContracts.PaymentLineItem {
                    ProductId = item.Key.ToString("D"),
                    Description = product.Name,
                    Quantity = item.Value,
                    UnitPriceInCents = product.PriceInCents
                };
            }));

            var payment = await billing.ProcessPaymentAsync(
                paymentRequest,
                cancellationToken: cancellationToken);

            auditState.PaymentSucceeded = payment.Success;
            auditState.PaymentProvider = payment.Provider;
            auditState.TransactionId = NullIfEmpty(payment.TransactionId);
            auditState.InvoiceId = NullIfEmpty(payment.InvoiceId);
            auditState.InvoiceQueued = payment.InvoiceQueued;
            auditState.Message = payment.Message;
            auditState.Phase = payment.Success
                ? "PAYMENT_COMPLETED"
                : "PAYMENT_FAILED";
            if (!payment.Success) {
                auditState.FailureReason = payment.Message;
            }
            await RecordAuditAsync(
                AuditEventType.PAYMENT,
                "ShopService",
                payment.Provider,
                payment.Success
                    ? AuditStatusCode.SUCCESS
                    : AuditStatusCode.FAILURE,
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
                    invoiceId = NullIfEmpty(payment.InvoiceId),
                    payment.InvoiceQueued,
                    totalInCents,
                    currency = "EUR"
                });

            auditState.Phase = "ORDER_COMPLETED";
            auditState.Message = "Zahlung und Warenreservierung wurden erfolgreich abgeschlossen.";
            await RecordAuditAsync(
                AuditEventType.ORDER_COMPLETED,
                "ShopService",
                "ShopService",
                AuditStatusCode.SUCCESS,
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
                    payment.Provider,
                    NullIfEmpty(payment.InvoiceId),
                    payment.InvoiceQueued && !string.IsNullOrWhiteSpace(payment.InvoiceId)
                        ? $"/api/invoices/{payment.InvoiceId}/pdf"
                        : null));
        }
        catch (RpcException exception) {
            auditState.PaymentSucceeded = false;
            auditState.Phase = "PAYMENT_FAILED";
            auditState.FailureReason = "Der BillingService ist nicht erreichbar.";
            auditState.Message = auditState.FailureReason;
            await RecordAuditAsync(
                AuditEventType.PAYMENT,
                "ShopService",
                "ShopService",
                AuditStatusCode.FAILURE,
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
            AuditEventType.STOCK_RELEASE,
            "ShopService",
            "ShopService",
            AuditStatusCode.COMPENSATING,
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
                AuditEventType.STOCK_RELEASE,
                "ShopService",
                "WarehouseService",
                response.Success
                    ? AuditStatusCode.COMPENSATED
                    : AuditStatusCode.FAILURE,
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
                AuditEventType.STOCK_RELEASE,
                "ShopService",
                "ShopService",
                AuditStatusCode.FAILURE,
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
            AuditEventType.ORDER_COMPLETED,
            "ShopService",
            "ShopService",
            AuditStatusCode.FAILURE,
            auditState,
            cancellationToken);

        return Failed(statusCode, message, totalInCents);
    }

    private Task RecordAuditAsync(
        AuditEventType eventType,
        string responsibleService,
        string actor,
        AuditStatusCode statusCode,
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
        IReadOnlyList<CheckoutItemRequest>? items,
        string? paymentProvider,
        string? customerEmail) {

        if (items is null || items.Count == 0) {
            return new ValidationResult(false, "Der Warenkorb ist leer.", null);
        }

        if (string.IsNullOrWhiteSpace(paymentProvider)) {
            return new ValidationResult(
                false,
                "Bitte wählen Sie einen Zahlungsanbieter aus.",
                null);
        }

        if (!MailAddress.TryCreate(customerEmail, out _)) {
            return new ValidationResult(
                false,
                "Bitte geben Sie eine gültige E-Mail-Adresse für die Rechnung an.",
                null);
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
                null,
                null,
                null));

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? GetRecipientDomain(string? email) =>
        MailAddress.TryCreate(email, out var address) ? address.Host : null;

    private sealed record ValidationResult(
        bool Success,
        string Message,
        IReadOnlyDictionary<Guid, int>? Quantities);

    private sealed class OrderAuditState(
        IReadOnlyList<CheckoutItemRequest> requestedItems,
        string? requestedPaymentProvider,
        string? customerEmailDomain) {

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
        public string? PaymentProviderKey { get; set; } = requestedPaymentProvider;
        public string? PaymentProvider { get; set; }
        public string? TransactionId { get; set; }
        public string? InvoiceId { get; set; }
        public bool? InvoiceQueued { get; set; }
        public bool CustomerEmailSupplied { get; } = customerEmailDomain is not null;
        public string? CustomerEmailDomain { get; } = customerEmailDomain;
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
