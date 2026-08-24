using System.Net.Mail;
using Grpc.Core;
using Microsoft.Extensions.Options;
using ShopService.Orchestration;
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
    IOptions<ShopServiceTimeoutOptions> configuredTimeouts,
    IStructuredLogger logger) {

    private readonly ShopServiceTimeoutOptions _timeouts = configuredTimeouts.Value;

    public async Task<CheckoutOutcome> CheckoutAsync(
        CheckoutRequest request,
        Guid orderId,
        CancellationToken cancellationToken = default) {

        ArgumentOutOfRangeException.ThrowIfEqual(orderId, Guid.Empty);

        var customerEmail = request.CustomerEmail?.Trim();
        var auditState = new OrderAuditState(
            orderId,
            request.Items ?? [],
            GetRecipientDomain(customerEmail));

        try {
            return await CheckoutCoreAsync(
                request,
                customerEmail,
                auditState,
                cancellationToken);
        }
        catch (Exception exception)
            when (IsRequestCancellation(exception, cancellationToken)) {
            TryLogCancellation(exception, auditState);
            await CompensateUnexpectedReservationAsync(auditState);

            auditState.Phase = "ORDER_CANCELLED";
            auditState.OrderStatus = "CANCELLED";
            auditState.FailureReason = "Der Bestellvorgang wurde abgebrochen.";
            auditState.Message = auditState.FailureReason;
            await RecordAuditAsync(
                AuditEventType.ORDER_COMPLETED,
                "ShopService",
                "ShopService",
                AuditStatusCode.FAILURE,
                auditState,
                CancellationToken.None);
            throw;
        }
        catch (Exception exception) {
            TryLogUnexpectedError(exception, auditState);
            await CompensateUnexpectedReservationAsync(auditState);

            auditState.Phase = "ORDER_UNEXPECTED_ERROR";
            auditState.OrderStatus = "FAILED";
            auditState.FailureReason = "Der Bestellvorgang ist aufgrund eines unerwarteten Fehlers fehlgeschlagen.";
            auditState.Message = auditState.FailureReason;
            await RecordAuditAsync(
                AuditEventType.ORDER_COMPLETED,
                "ShopService",
                "ShopService",
                AuditStatusCode.FAILURE,
                auditState,
                CancellationToken.None);
            throw;
        }
    }

    private async Task<CheckoutOutcome> CheckoutCoreAsync(
        CheckoutRequest request,
        string? customerEmail,
        OrderAuditState auditState,
        CancellationToken cancellationToken) {

        await RecordAuditAsync(
            AuditEventType.ORDER_STARTED,
            "ShopService",
            "ANONYMOUS_USER",
            AuditStatusCode.SUCCESS,
            auditState,
            cancellationToken);

        var validation = ValidateAndGroup(
            request.Items,
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
                StatusCodes.Status422UnprocessableEntity,
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

        long totalInCents = 0;
        var stockRequest = new WarehouseContracts.CartStockRequest {
            ReservationId = auditState.OrderId.ToString("D")
        };
        stockRequest.Items.AddRange(quantities.Select(item =>
            new WarehouseContracts.CartProductQuantity {
                ProductId = item.Key.ToString(),
                Quantity = item.Value
            }));

        WarehouseContracts.CartStockResponse reservation;
        try {
            reservation = await warehouse.ReserveCartAsync(
                stockRequest,
                deadline: DateTime.UtcNow.Add(_timeouts.StockOperation),
                cancellationToken: cancellationToken);
        }
        catch (RpcException exception)
            when (!IsRequestCancellation(exception, cancellationToken)) {
            var failure = GetDownstreamFailure("WarehouseService", exception);
            auditState.Phase = "STOCK_RESERVATION_FAILED";
            auditState.FailureReason = failure.Message;
            auditState.Message = auditState.FailureReason;
            await RecordAuditAsync(
                AuditEventType.STOCK_RESERVATION,
                "ShopService",
                "ShopService",
                AuditStatusCode.FAILURE,
                auditState,
                cancellationToken);
            TryLogDownstreamError(
                exception,
                "WarehouseService",
                "ReserveCart",
                auditState);

            return await CompleteWithFailureAsync(
                auditState,
                failure.StatusCode,
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

        var products = new Dictionary<Guid, WarehouseContracts.CartProductStock>();
        var reservationResponseIsValid = reservation.Products.Count == quantities.Count;
        foreach (var product in reservation.Products) {
            if (!Guid.TryParse(product.ProductId, out var productId) ||
                !quantities.ContainsKey(productId) ||
                string.IsNullOrWhiteSpace(product.Name) ||
                product.PriceInCents < 0 ||
                !products.TryAdd(productId, product)) {
                reservationResponseIsValid = false;
            }
        }

        if (!reservationResponseIsValid ||
            quantities.Keys.Any(productId => !products.ContainsKey(productId))) {
            auditState.Phase = "STOCK_RESERVATION_RESPONSE_INVALID";
            auditState.FailureReason =
                "Der WarehouseService hat unvollständige Produktdaten für die Reservierung geliefert.";
            auditState.Message = auditState.FailureReason;
            await RecordAuditAsync(
                AuditEventType.STOCK_RESERVATION,
                "ShopService",
                "WarehouseService",
                AuditStatusCode.FAILURE,
                auditState,
                cancellationToken);
            await ReleaseReservationAsync(
                stockRequest,
                auditState,
                cancellationToken);
            TryLogError(null, auditState.FailureReason);
            return await CompleteWithFailureAsync(
                auditState,
                StatusCodes.Status502BadGateway,
                "Die reservierten Produktdaten konnten nicht verarbeitet werden. " +
                "Die Reservierung wurde zurückgenommen.",
                cancellationToken);
        }

        foreach (var (productId, quantity) in quantities) {
            totalInCents = checked(
                totalInCents + checked(products[productId].PriceInCents * quantity));
        }
        auditState.TotalInCents = totalInCents;

        BillingContracts.PaymentProviderInfo? selectedPaymentProvider;
        try {
            var availableProviders = await billing.ListPaymentProvidersAsync(
                new BillingContracts.PaymentProvidersRequest(),
                deadline: DateTime.UtcNow.Add(_timeouts.CatalogQuery),
                cancellationToken: cancellationToken);
            selectedPaymentProvider = availableProviders.Providers.SingleOrDefault(provider =>
                provider.IsActive);

            if (selectedPaymentProvider is null) {
                auditState.Phase = "PAYMENT_PROVIDER_CONFIGURATION_INVALID";
                auditState.FailureReason =
                    "Der zentral konfigurierte Zahlungsanbieter ist nicht verfügbar.";
                auditState.Message = auditState.FailureReason;
                await RecordAuditAsync(
                    AuditEventType.PAYMENT,
                    "ShopService",
                    "BillingService",
                    AuditStatusCode.FAILURE,
                    auditState,
                    cancellationToken);
                await ReleaseReservationAsync(
                    stockRequest,
                    auditState,
                    cancellationToken);
                logger.Warn(
                    "Checkout rejected an invalid active payment provider configuration.");
                return await CompleteWithFailureAsync(
                    auditState,
                    StatusCodes.Status422UnprocessableEntity,
                    auditState.FailureReason,
                    cancellationToken,
                    totalInCents);
            }
        }
        catch (RpcException exception)
            when (!IsRequestCancellation(exception, cancellationToken)) {
            var failure = GetDownstreamFailure("BillingService", exception);
            auditState.Phase = "PAYMENT_PROVIDER_LOOKUP_FAILED";
            auditState.FailureReason = failure.Message;
            auditState.Message = auditState.FailureReason;
            await RecordAuditAsync(
                AuditEventType.PAYMENT,
                "ShopService",
                "BillingService",
                AuditStatusCode.FAILURE,
                auditState,
                cancellationToken);
            await ReleaseReservationAsync(
                stockRequest,
                auditState,
                cancellationToken);
            TryLogDownstreamError(
                exception,
                "BillingService",
                "ListPaymentProviders",
                auditState);
            return await CompleteWithFailureAsync(
                auditState,
                failure.StatusCode,
                $"{auditState.FailureReason} Die Reservierung wurde zurückgenommen.",
                cancellationToken,
                totalInCents);
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

        var reference = $"HOLZWERK-{Guid.NewGuid():N}";
        auditState.Reference = reference;
        try {
            var paymentRequest = new BillingContracts.PaymentRequest {
                OrderId = auditState.OrderId.ToString("D"),
                AmountInCents = totalInCents,
                Currency = "EUR",
                PaymentMethod = "test",
                Reference = reference,
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
                deadline: DateTime.UtcNow.Add(_timeouts.PaymentOperation),
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
                    StatusCodes.Status422UnprocessableEntity,
                    payment.Message,
                    cancellationToken,
                    totalInCents);
            }

            if (!payment.InvoiceQueued || string.IsNullOrWhiteSpace(payment.InvoiceId)) {
                auditState.Phase = "INVOICE_QUEUE_FAILED";
                auditState.FailureReason =
                    "Das PaymentSucceeded-Ereignis konnte nicht zur Rechnungserstellung veröffentlicht werden.";
                auditState.Message = auditState.FailureReason;
                await RecordAuditAsync(
                    AuditEventType.INVOICE,
                    "ShopService",
                    "BillingService",
                    AuditStatusCode.FAILURE,
                    auditState,
                    cancellationToken);

                var compensated = await CompensatePaymentAsync(
                    payment.TransactionId,
                    totalInCents,
                    stockRequest,
                    auditState);
                var message = compensated
                    ? "Die Rechnung konnte nicht eingeplant werden. Zahlung und Reservierung wurden zurückgenommen."
                    : "Die Rechnung konnte nicht eingeplant werden. Die automatische Kompensation ist fehlgeschlagen; " +
                      "der Betreiber wurde informiert.";
                return await CompleteWithFailureAsync(
                    auditState,
                    StatusCodes.Status503ServiceUnavailable,
                    message,
                    CancellationToken.None,
                    totalInCents);
            }

            WarehouseContracts.CartStockResponse commit;
            try {
                commit = await CommitReservationWithRetryAsync(
                    stockRequest,
                    auditState,
                    cancellationToken);
            }
            catch (RpcException exception)
                when (!IsRequestCancellation(exception, cancellationToken)) {
                var failure = GetDownstreamFailure("WarehouseService", exception);
                auditState.StockCommitted = false;
                auditState.Phase = "STOCK_COMMIT_FAILED";
                auditState.FailureReason = failure.Message;
                auditState.Message =
                    "Die Zahlung war erfolgreich, die endgültige Lagerbuchung konnte aber nicht bestätigt werden.";
                await RecordAuditAsync(
                    AuditEventType.STOCK_RESERVATION,
                    "ShopService",
                    "WarehouseService",
                    AuditStatusCode.FAILURE,
                    auditState,
                    CancellationToken.None);
                TryLogDownstreamError(
                    exception,
                    "WarehouseService",
                    "CommitCart",
                    auditState);

                return await CompleteWithFailureAsync(
                    auditState,
                    failure.StatusCode,
                    $"{auditState.Message} Der Betreiber wurde über den Fehler informiert.",
                    CancellationToken.None,
                    totalInCents);
            }

            auditState.StockCommitted = commit.Success;
            auditState.Stock = commit.Products
                .Select(product => new ProductStockSnapshot(
                    product.ProductId,
                    product.AvailableQuantity,
                    product.IsSoldOut))
                .ToArray();
            auditState.Phase = commit.Success
                ? "STOCK_COMMITTED"
                : "STOCK_COMMIT_FAILED";
            auditState.Message = commit.Message;
            if (!commit.Success) {
                auditState.FailureReason = commit.Message;
            }
            await RecordAuditAsync(
                AuditEventType.STOCK_RESERVATION,
                "ShopService",
                "WarehouseService",
                commit.Success
                    ? AuditStatusCode.SUCCESS
                    : AuditStatusCode.FAILURE,
                auditState,
                cancellationToken);

            if (!commit.Success) {
                TryLogError(
                    null,
                    $"Lagerreservierung konnte nicht endgültig ausgebucht werden: {commit.Message}");
                return await CompleteWithFailureAsync(
                    auditState,
                    StatusCodes.Status502BadGateway,
                    "Die Zahlung war erfolgreich, die endgültige Lagerbuchung ist jedoch fehlgeschlagen. " +
                    "Der Betreiber wurde über den Fehler informiert.",
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
                    stockCommitted = commit.Success,
                    totalInCents,
                    currency = "EUR"
                });

            auditState.Phase = "ORDER_COMPLETED";
            auditState.OrderStatus = "COMPLETED";
            auditState.Message =
                "Zahlung, Rechnungsevent und endgültige Lagerbuchung wurden erfolgreich abgeschlossen.";
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
                    auditState.OrderId.ToString("D"),
                    "Vielen Dank! Die Bestellung wurde erfolgreich abgeschlossen.",
                    totalInCents / 100m,
                    "EUR",
                    payment.TransactionId,
                    payment.Provider,
                    NullIfEmpty(payment.InvoiceId),
                    payment.InvoiceQueued && !string.IsNullOrWhiteSpace(payment.InvoiceId)
                        ? $"/api/invoices/{payment.InvoiceId}/pdf"
                        : null));
        }
        catch (RpcException exception)
            when (!IsRequestCancellation(exception, cancellationToken)) {
            var failure = GetDownstreamFailure("BillingService", exception);
            auditState.PaymentSucceeded = false;
            auditState.Phase = "PAYMENT_FAILED";
            auditState.FailureReason = failure.Message;
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
            TryLogDownstreamError(
                exception,
                "BillingService",
                "ProcessPayment",
                auditState);
            return await CompleteWithFailureAsync(
                auditState,
                failure.StatusCode,
                $"{failure.Message} Die Reservierung wurde zurückgenommen.",
                cancellationToken,
                totalInCents);
        }
    }

    private async Task<bool> CompensatePaymentAsync(
        string transactionId,
        long amountInCents,
        WarehouseContracts.CartStockRequest stockRequest,
        OrderAuditState auditState) {

        auditState.Phase = "PAYMENT_REFUND_REQUESTED";
        auditState.Message =
            "Die Zahlung wird wegen des fehlgeschlagenen Rechnungsevents zurückgenommen.";
        await RecordAuditAsync(
            AuditEventType.PAYMENT,
            "ShopService",
            "ShopService",
            AuditStatusCode.COMPENSATING,
            auditState,
            CancellationToken.None);

        try {
            var refund = await billing.RefundPaymentAsync(
                new BillingContracts.RefundPaymentRequest {
                    TransactionId = transactionId,
                    AmountInCents = amountInCents
                },
                deadline: DateTime.UtcNow.Add(_timeouts.Compensation),
                cancellationToken: CancellationToken.None);

            auditState.Phase = refund.Success
                ? "PAYMENT_REFUNDED"
                : "PAYMENT_REFUND_FAILED";
            auditState.Message = refund.Message;
            await RecordAuditAsync(
                AuditEventType.PAYMENT,
                "ShopService",
                "BillingService",
                refund.Success
                    ? AuditStatusCode.COMPENSATED
                    : AuditStatusCode.FAILURE,
                auditState,
                CancellationToken.None);

            if (!refund.Success) {
                TryLogError(
                    null,
                    $"Zahlung konnte nach fehlgeschlagenem Rechnungsevent nicht erstattet werden: {refund.Message}");
                return false;
            }

            await ReleaseReservationAsync(
                stockRequest,
                auditState,
                CancellationToken.None);
            return auditState.ReservationSucceeded is false;
        }
        catch (RpcException exception) {
            auditState.Phase = "PAYMENT_REFUND_FAILED";
            auditState.Message =
                "Die Zahlung konnte nach dem fehlgeschlagenen Rechnungsevent nicht erstattet werden.";
            await RecordAuditAsync(
                AuditEventType.PAYMENT,
                "ShopService",
                "BillingService",
                AuditStatusCode.FAILURE,
                auditState,
                CancellationToken.None);
            TryLogDownstreamError(
                exception,
                "BillingService",
                "RefundPayment",
                auditState);
            return false;
        }
    }

    private async Task<WarehouseContracts.CartStockResponse> CommitReservationWithRetryAsync(
        WarehouseContracts.CartStockRequest request,
        OrderAuditState auditState,
        CancellationToken cancellationToken) {

        try {
            return await warehouse.CommitCartAsync(
                request,
                deadline: DateTime.UtcNow.Add(_timeouts.StockOperation),
                cancellationToken: cancellationToken);
        }
        catch (RpcException exception)
            when (!IsRequestCancellation(exception, cancellationToken) &&
                  exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded) {
            auditState.Phase = "STOCK_COMMIT_RETRY";
            auditState.Message =
                "Die Bestätigung der Lagerbuchung wird nach einem Kommunikationsfehler wiederholt.";
            await RecordAuditAsync(
                AuditEventType.STOCK_RESERVATION,
                "ShopService",
                "WarehouseService",
                AuditStatusCode.FAILURE,
                auditState,
                CancellationToken.None);
            try {
                logger.Warn(
                    "Warehouse stock commit will be retried.",
                    new {
                        operation = "CommitCart",
                        reservationId = request.ReservationId,
                        attempt = 2,
                        grpcStatus = exception.StatusCode.ToString(),
                        grpcDetail = exception.Status.Detail
                    },
                    exception);
            }
            catch (Exception) {
                // Eine nicht verfügbare Log-Senke darf den idempotenten
                // Wiederholungsversuch nicht verhindern.
            }

            return await warehouse.CommitCartAsync(
                request,
                deadline: DateTime.UtcNow.Add(_timeouts.StockOperation),
                cancellationToken: CancellationToken.None);
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
                deadline: DateTime.UtcNow.Add(_timeouts.Compensation),
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
            auditState.FailureReason = "Reservierung konnte nach Abrechnungsfehler nicht zurückgenommen werden.";
            auditState.Message = "Reservierung konnte nach Abrechnungsfehler nicht zurückgenommen werden.";
            await RecordAuditAsync(
                AuditEventType.STOCK_RELEASE,
                "ShopService",
                "ShopService",
                AuditStatusCode.FAILURE,
                auditState,
                CancellationToken.None);
            TryLogDownstreamError(
                exception,
                "WarehouseService",
                "ReleaseCart",
                auditState);
        }
    }

    private async Task CompensateUnexpectedReservationAsync(
        OrderAuditState auditState) {

        if (auditState.ReservationSucceeded is not true ||
            auditState.PaymentSucceeded is true) {
            return;
        }

        var request = new WarehouseContracts.CartStockRequest {
            ReservationId = auditState.OrderId.ToString("D")
        };
        request.Items.AddRange(auditState.Items.Select(item =>
            new WarehouseContracts.CartProductQuantity {
                ProductId = item.ProductId,
                Quantity = item.Quantity
            }));

        await ReleaseReservationAsync(
            request,
            auditState,
            CancellationToken.None);
    }

    private async Task<CheckoutOutcome> CompleteWithFailureAsync(
        OrderAuditState auditState,
        int statusCode,
        string message,
        CancellationToken cancellationToken,
        long totalInCents = 0) {

        auditState.Phase = "ORDER_FAILED";
        auditState.OrderStatus = "FAILED";
        auditState.FailureReason ??= message;
        auditState.Message = message;
        await RecordAuditAsync(
            AuditEventType.ORDER_COMPLETED,
            "ShopService",
            "ShopService",
            AuditStatusCode.FAILURE,
            auditState,
            cancellationToken);

        return Failed(auditState.OrderId, statusCode, message, totalInCents);
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

    private void TryLogDownstreamError(
        RpcException exception,
        string downstreamService,
        string operation,
        OrderAuditState auditState) {

        try {
            logger.Error(
                "Downstream service call failed.",
                new {
                    downstreamService,
                    operation,
                    orderId = auditState.OrderId,
                    auditState.Reference,
                    auditState.Phase,
                    grpcStatus = exception.StatusCode.ToString(),
                    grpcDetail = exception.Status.Detail,
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

    private void TryLogCancellation(
        Exception exception,
        OrderAuditState auditState) {

        try {
            logger.Warn(
                "Checkout was cancelled by the client.",
                new {
                    orderId = auditState.OrderId,
                    auditState.Reference,
                    auditState.Phase,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);
        }
        catch (Exception) {
            // Siehe TryLogDownstreamError.
        }
    }

    private void TryLogUnexpectedError(
        Exception exception,
        OrderAuditState auditState) {

        try {
            logger.Error(
                "Checkout failed unexpectedly.",
                new {
                    orderId = auditState.OrderId,
                    auditState.Reference,
                    auditState.Phase,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);
        }
        catch (Exception) {
            // Siehe TryLogDownstreamError.
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
        string? customerEmail) {

        if (items is null || items.Count == 0) {
            return new ValidationResult(false, "Der Warenkorb ist leer.", null);
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

    private static DownstreamFailure GetDownstreamFailure(
        string downstreamService,
        RpcException exception) =>
        exception.StatusCode switch {
            StatusCode.Unavailable => new DownstreamFailure(
                StatusCodes.Status503ServiceUnavailable,
                $"Der {downstreamService} ist nicht erreichbar."),
            StatusCode.DeadlineExceeded => new DownstreamFailure(
                StatusCodes.Status504GatewayTimeout,
                $"Der {downstreamService} hat nicht rechtzeitig geantwortet."),
            _ => new DownstreamFailure(
                StatusCodes.Status502BadGateway,
                $"Der {downstreamService} konnte die Anfrage nicht verarbeiten.")
        };

    private static bool IsRequestCancellation(
        Exception exception,
        CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested &&
        (exception is OperationCanceledException ||
         exception is RpcException { StatusCode: StatusCode.Cancelled });

    private static CheckoutOutcome Failed(
        Guid orderId,
        int statusCode,
        string message,
        long totalInCents = 0) =>
        new(
            statusCode,
            new CheckoutResponse(
                false,
                orderId.ToString("D"),
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

    private sealed record DownstreamFailure(
        int StatusCode,
        string Message);

    private sealed class OrderAuditState(
        Guid orderId,
        IReadOnlyList<CheckoutItemRequest> requestedItems,
        string? customerEmailDomain) {

        public Guid OrderId { get; } = orderId;
        public string OrderStatus { get; set; } = "IN_PROGRESS";
        public string Phase { get; set; } = "ORDER_STARTED";
        public IReadOnlyList<OrderItemSnapshot> Items { get; set; } = requestedItems
            .Select(item => new OrderItemSnapshot(item.ProductId, item.Quantity))
            .ToArray();
        public long? TotalInCents { get; set; }
        public string Currency { get; } = "EUR";
        public string? Reference { get; set; }
        public bool? ReservationSucceeded { get; set; }
        public bool? StockCommitted { get; set; }
        public IReadOnlyList<ProductStockSnapshot> Stock { get; set; } = [];
        public bool? PaymentSucceeded { get; set; }
        public string? PaymentProviderKey { get; set; }
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
