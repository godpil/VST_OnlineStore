using System.Net.Mail;
using BillingService.Messaging;
using BillingService.Payments;
using Grpc.Core;
using Microsoft.Extensions.Options;
using VstOnlineStore.Contracts.BillingService;
using VstOnlineStore.Messaging;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;
using VstOnlineStore.Presentation;
using ContractPaymentStatus = VstOnlineStore.Contracts.BillingService.PaymentTransactionStatus;
using DomainPaymentStatus = BillingService.Payments.PaymentTransactionStatus;

namespace BillingService.GrpcServices;

public sealed class BillingOperationsGrpcService(
    IPaymentFacade paymentFacade,
    IPaymentSucceededEventPublisher invoiceEvents,
    IAuditEventPublisher audit,
    IStructuredLogger logger,
    IOptions<PresentationModeOptions> configuredPresentationMode)
    : BillingOperations.BillingOperationsBase {

    private readonly PresentationModeOptions _presentationMode =
        configuredPresentationMode.Value;

    public override async Task<PaymentResponse> ProcessPayment(
        PaymentRequest request,
        ServerCallContext context) {

        PaymentProviderDescriptor paymentProvider;
        try {
            paymentProvider = paymentFacade.GetProvider(request.ProviderKey);
        }
        catch (ArgumentException exception) {
            const string message = "Der gewählte Zahlungsanbieter ist nicht verfügbar.";
            logger.Warn(
                "Payment request selected an unavailable provider.",
                new {
                    requestedProviderKey = request.ProviderKey,
                    request.OrderId,
                    request.Reference
                },
                exception);
            await audit.PublishAsync(
                AuditEventType.PAYMENT,
                "BillingService",
                new {
                    phase = "PAYMENT_PROVIDER_UNAVAILABLE",
                    request.OrderId,
                    requestedProviderKey = request.ProviderKey,
                    success = false,
                    message
                },
                "BillingService",
                AuditStatusCode.FAILURE,
                cancellationToken: context.CancellationToken);
            return new PaymentResponse {
                Success = false,
                Provider = request.ProviderKey,
                Message = message
            };
        }
        logger.Info(
            "Payment provider selected for request.",
            new {
                providerKey = paymentProvider.Key,
                providerName = paymentProvider.Name,
                paymentProvider.IsTestMode,
                request.OrderId,
                request.Reference,
                request.AmountInCents,
                request.Currency
            });

        if (!IsPaymentRequestValid(request, out var orderId)) {
            const string message =
                "Bestell-ID, Betrag, Währung, E-Mail-Adresse und Rechnungspositionen müssen gültig sein.";
            logger.Warn(
                "Payment request validation failed.",
                new {
                    providerKey = paymentProvider.Key,
                    providerName = paymentProvider.Name,
                    request.OrderId,
                    request.Reference,
                    request.AmountInCents,
                    request.Currency
                });
            await audit.PublishAsync(
                AuditEventType.PAYMENT,
                "BillingService",
                CreateAuditPayload(
                    request,
                    paymentProvider,
                    false,
                    null,
                    message),
                paymentProvider.Name,
                AuditStatusCode.FAILURE,
                cancellationToken: context.CancellationToken);
            return new PaymentResponse {
                Success = false,
                Provider = paymentProvider.Name,
                Message = message
            };
        }

        PaymentChargeResult result;
        if (_presentationMode.Enabled && PresentationScenarios.Is(
                request.PresentationScenario,
                PresentationScenarios.PaymentDeclined)) {
            result = new PaymentChargeResult(
                false,
                string.Empty,
                DomainPaymentStatus.Failed,
                "Die Zahlung wurde für das Vorführszenario vom Anbieter abgelehnt.");
            logger.Warn(
                "Payment provider declined the presentation scenario charge.",
                new {
                    request.OrderId,
                    request.Reference,
                    providerKey = paymentProvider.Key,
                    presentationScenario = request.PresentationScenario
                });
        }
        else {
            try {
                result = await paymentFacade.ChargeAsync(
                    paymentProvider.Key,
                    orderId,
                    request.AmountInCents,
                    request.Currency,
                    context.CancellationToken);
            }
            catch (OperationCanceledException exception)
                when (context.CancellationToken.IsCancellationRequested) {
                const string message = "Der Zahlungsvorgang wurde abgebrochen.";
                logger.Warn(
                    "Payment facade call was cancelled by the client.",
                    new {
                        providerKey = paymentProvider.Key,
                        providerName = paymentProvider.Name,
                        request.OrderId,
                        request.Reference,
                        request.AmountInCents,
                        request.Currency,
                        exceptionType = exception.GetType().FullName,
                        exceptionMessage = exception.Message
                    },
                    exception);
                await audit.PublishAsync(
                    AuditEventType.PAYMENT,
                    "BillingService",
                    CreateAuditPayload(
                        request,
                        paymentProvider,
                        false,
                        null,
                        message,
                        "PAYMENT_CANCELLED"),
                    paymentProvider.Name,
                    AuditStatusCode.FAILURE,
                    cancellationToken: CancellationToken.None);
                throw;
            }
            catch (Exception exception) {
                const string message = "Der Zahlungsanbieter konnte die Zahlung nicht verarbeiten.";
                logger.Error(
                    "Payment facade call failed.",
                    new {
                        providerKey = paymentProvider.Key,
                        providerName = paymentProvider.Name,
                        request.OrderId,
                        request.Reference,
                        request.AmountInCents,
                        request.Currency,
                        exceptionType = exception.GetType().FullName,
                        exceptionMessage = exception.Message
                    },
                    exception);
                await audit.PublishAsync(
                    AuditEventType.PAYMENT,
                    "BillingService",
                    CreateAuditPayload(
                        request,
                        paymentProvider,
                        false,
                        null,
                        message,
                        "PAYMENT_PROVIDER_ERROR"),
                    paymentProvider.Name,
                    AuditStatusCode.FAILURE,
                    cancellationToken: context.CancellationToken);
                return new PaymentResponse {
                    Success = false,
                    Provider = paymentProvider.Name,
                    Message = message
                };
            }
        }

        await audit.PublishAsync(
            AuditEventType.PAYMENT,
            "BillingService",
            CreateAuditPayload(
                request,
                paymentProvider,
                result.Success,
                result.TransactionId,
                result.Message),
            paymentProvider.Name,
            result.Success ? AuditStatusCode.SUCCESS : AuditStatusCode.FAILURE,
            cancellationToken: context.CancellationToken);

        var invoiceId = Guid.Empty;
        var invoiceQueued = false;
        if (result.Success) {
            invoiceId = Guid.NewGuid();
            var correlationId = GetCorrelationId(context);
            var paymentEvent = new PaymentSucceededEvent(
                Guid.NewGuid(),
                invoiceId,
                correlationId,
                DateTime.UtcNow,
                request.Reference,
                request.CustomerEmail,
                request.AmountInCents,
                request.Currency,
                paymentProvider.Name,
                result.TransactionId,
                request.InvoiceItems.Select(item => new PaymentSucceededLineItem(
                    item.ProductId,
                    item.Description,
                    item.Quantity,
                    item.UnitPriceInCents)).ToArray(),
                NullIfEmpty(request.PresentationScenario));
            invoiceQueued = await invoiceEvents.PublishAsync(
                paymentEvent,
                CancellationToken.None);

            await audit.PublishAsync(
                AuditEventType.INVOICE,
                "BillingService",
                new {
                    phase = invoiceQueued ? "INVOICE_QUEUED" : "INVOICE_QUEUE_FAILED",
                    invoiceId,
                    request.OrderId,
                    request.Reference,
                    paymentProvider = paymentProvider.Name,
                    result.TransactionId,
                    recipientDomain = GetRecipientDomain(request.CustomerEmail),
                    invoiceQueued
                },
                "BillingService",
                invoiceQueued ? AuditStatusCode.SUCCESS : AuditStatusCode.FAILURE,
                cancellationToken: context.CancellationToken);
        }

        var responseMessage = result.Success && !invoiceQueued
            ? $"{result.Message} Die Rechnung konnte noch nicht zur Erstellung eingeplant werden."
            : result.Message;

        return new PaymentResponse {
            Success = result.Success,
            TransactionId = result.TransactionId,
            Provider = paymentProvider.Name,
            Message = responseMessage,
            InvoiceId = invoiceQueued ? invoiceId.ToString("D") : string.Empty,
            InvoiceQueued = invoiceQueued
        };
    }

    public override async Task<RefundPaymentResponse> RefundPayment(
        RefundPaymentRequest request,
        ServerCallContext context) {

        if (string.IsNullOrWhiteSpace(request.TransactionId)
            || request.AmountInCents <= 0) {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "Transaktions-ID und Erstattungsbetrag müssen gültig sein."));
        }

        var result = await paymentFacade.RefundAsync(
            request.TransactionId,
            request.AmountInCents,
            context.CancellationToken);
        logger.Info(
            "Payment refund processed by facade.",
            new {
                request.TransactionId,
                request.AmountInCents,
                result.Success,
                result.TotalRefundedAmountInCents,
                status = result.Status.ToString()
            });

        await audit.PublishAsync(
            AuditEventType.PAYMENT,
            "BillingService",
            new {
                phase = result.Success ? "PAYMENT_REFUNDED" : "PAYMENT_REFUND_FAILED",
                request.TransactionId,
                request.AmountInCents,
                result.TotalRefundedAmountInCents,
                status = result.Status.ToString(),
                result.Message
            },
            "BillingService",
            result.Success ? AuditStatusCode.COMPENSATED : AuditStatusCode.FAILURE,
            cancellationToken: context.CancellationToken);

        return new RefundPaymentResponse {
            Success = result.Success,
            TransactionId = result.TransactionId,
            RefundedAmountInCents = result.RefundedAmountInCents,
            TotalRefundedAmountInCents = result.TotalRefundedAmountInCents,
            Status = ToContractStatus(result.Status),
            Message = result.Message
        };
    }

    public override async Task<PaymentStatusResponse> GetPaymentStatus(
        PaymentStatusRequest request,
        ServerCallContext context) {

        if (string.IsNullOrWhiteSpace(request.TransactionId)) {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "Die Transaktions-ID muss angegeben werden."));
        }

        var result = await paymentFacade.GetStatusAsync(
            request.TransactionId,
            context.CancellationToken);
        return new PaymentStatusResponse {
            Found = result.Found,
            TransactionId = result.TransactionId,
            OrderId = result.OrderId == Guid.Empty
                ? string.Empty
                : result.OrderId.ToString("D"),
            AmountInCents = result.AmountInCents,
            RefundedAmountInCents = result.RefundedAmountInCents,
            Currency = result.Currency,
            Status = ToContractStatus(result.Status),
            Message = result.Message
        };
    }

    public override Task<PaymentProvidersResponse> ListPaymentProviders(
        PaymentProvidersRequest request,
        ServerCallContext context) {

        var response = new PaymentProvidersResponse();
        response.Providers.AddRange(paymentFacade.Providers.Select(provider =>
            new PaymentProviderInfo {
                Key = provider.Key,
                Name = provider.Name,
                IsTestMode = provider.IsTestMode,
                IsActive = provider.IsActive,
                IsEnabled = provider.IsEnabled
            }));

        logger.Debug(
            "Payment provider list requested.",
            new {
                providerCount = response.Providers.Count,
                activeProvider = paymentFacade.ActiveProvider.Key,
                providers = response.Providers.Select(provider => provider.Key).ToArray()
            });
        return Task.FromResult(response);
    }

    public override Task<BillingStatusResponse> GetStatus(
        BillingStatusRequest request,
        ServerCallContext context) =>
        Task.FromResult(new BillingStatusResponse {
            Available = true,
            Service = "BillingService"
        });

    private static object CreateAuditPayload(
        PaymentRequest request,
        PaymentProviderDescriptor provider,
        bool success,
        string? transactionId,
        string message,
        string? phase = null) => new {
            phase = phase ?? (success ? "PAYMENT_COMPLETED" : "PAYMENT_FAILED"),
            request.OrderId,
            request.AmountInCents,
            request.Currency,
            paymentMethodSupplied = !string.IsNullOrWhiteSpace(request.PaymentMethod),
            request.Reference,
            requestedProviderKey = request.ProviderKey,
            presentationScenario = NullIfEmpty(request.PresentationScenario),
            providerKey = provider.Key,
            provider = provider.Name,
            provider.IsTestMode,
            customerEmailSupplied = !string.IsNullOrWhiteSpace(request.CustomerEmail),
            recipientDomain = GetRecipientDomain(request.CustomerEmail),
            invoiceItemCount = request.InvoiceItems.Count,
            success,
            transactionId,
            message
        };

    private static bool IsPaymentRequestValid(
        PaymentRequest request,
        out Guid orderId) {

        if (!Guid.TryParse(request.OrderId, out orderId)
            || orderId == Guid.Empty
            || request.AmountInCents <= 0
            || string.IsNullOrWhiteSpace(request.Currency)
            || !MailAddress.TryCreate(request.CustomerEmail, out _)
            || request.InvoiceItems.Count == 0
            || request.InvoiceItems.Any(item =>
                string.IsNullOrWhiteSpace(item.ProductId)
                || string.IsNullOrWhiteSpace(item.Description)
                || item.Quantity <= 0
                || item.UnitPriceInCents < 0)) {
            return false;
        }

        try {
            var invoiceTotal = request.InvoiceItems.Sum(item =>
                checked(item.UnitPriceInCents * item.Quantity));
            return invoiceTotal == request.AmountInCents;
        }
        catch (OverflowException) {
            return false;
        }
    }

    private static ContractPaymentStatus ToContractStatus(
        DomainPaymentStatus status) =>
        status switch {
            DomainPaymentStatus.Pending => ContractPaymentStatus.Pending,
            DomainPaymentStatus.Succeeded => ContractPaymentStatus.Succeeded,
            DomainPaymentStatus.Failed => ContractPaymentStatus.Failed,
            DomainPaymentStatus.PartiallyRefunded => ContractPaymentStatus.PartiallyRefunded,
            DomainPaymentStatus.Refunded => ContractPaymentStatus.Refunded,
            _ => ContractPaymentStatus.Unspecified
        };

    private static Guid GetCorrelationId(ServerCallContext context) {
        var httpContext = context.GetHttpContext();
        if (CorrelationId.TryGet(httpContext, out var correlationId)) {
            return correlationId;
        }

        throw new InvalidOperationException(
            "Für die Rechnungserstellung ist keine Correlation-ID verfügbar.");
    }

    private static string? GetRecipientDomain(string email) =>
        MailAddress.TryCreate(email, out var address) ? address.Host : null;

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
