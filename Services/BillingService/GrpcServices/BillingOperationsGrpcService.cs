using System.Net.Mail;
using BillingService.Messaging;
using BillingService.Payments;
using Grpc.Core;
using VstOnlineStore.Contracts.BillingService;
using VstOnlineStore.Messaging;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;

namespace BillingService.GrpcServices;

public sealed class BillingOperationsGrpcService(
    PaymentProviderResolver paymentProviders,
    IPaymentSucceededEventPublisher invoiceEvents,
    IAuditEventPublisher audit,
    IStructuredLogger logger) : BillingOperations.BillingOperationsBase {

    public override async Task<PaymentResponse> ProcessPayment(
        PaymentRequest request,
        ServerCallContext context) {

        var requestedProviderKey = request.PaymentProvider.Trim();

        if (!paymentProviders.TryResolve(requestedProviderKey, out var paymentProvider)) {
            const string message = "Der ausgewählte Zahlungsanbieter ist nicht verfügbar.";
            logger.Warn(
                "Unknown payment provider requested.",
                new {
                    requestedProviderKey,
                    request.Reference,
                    request.AmountInCents,
                    request.Currency
                });
            //YAYY Da sind die Payment Provider :D
            await audit.PublishAsync(
                AuditEventType.PAYMENT,
                "BillingService",
                CreateAuditPayload(
                    request,
                    requestedProviderKey,
                    null,
                    false,
                    null,
                    message,
                    "PAYMENT_PROVIDER_REJECTED"),
                "BillingService",
                AuditStatusCode.FAILURE,
                cancellationToken: context.CancellationToken);
            return new PaymentResponse {
                Success = false,
                Provider = requestedProviderKey,
                Message = message
            };
        }

        logger.Info(
            "Payment provider selected.",
            new {
                providerKey = paymentProvider.Key,
                providerName = paymentProvider.Name,
                paymentProvider.IsTestMode,
                request.Reference,
                request.AmountInCents,
                request.Currency
            });

        if (!IsPaymentRequestValid(request)) {
            const string message = "Betrag, Währung, E-Mail-Adresse und Rechnungspositionen müssen gültig sein.";
            logger.Warn(
                "Payment request validation failed.",
                new {
                    providerKey = paymentProvider.Key,
                    providerName = paymentProvider.Name,
                    request.Reference,
                    request.AmountInCents,
                    request.Currency
                });
            await audit.PublishAsync(
                AuditEventType.PAYMENT,
                "BillingService",
                CreateAuditPayload(
                    request,
                    paymentProvider.Key,
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

        PaymentProviderResult result;
        try {
            result = await paymentProviders.ChargeAsync(
                paymentProvider,
                request.AmountInCents,
                request.Currency,
                request.PaymentMethod,
                request.Reference,
                context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (Exception exception) {
            const string message = "Der Zahlungsanbieter konnte die Zahlung nicht verarbeiten.";
            logger.Error(
                "Payment provider call failed.",
                new {
                    providerKey = paymentProvider.Key,
                    providerName = paymentProvider.Name,
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
                    paymentProvider.Key,
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

        await audit.PublishAsync(
            AuditEventType.PAYMENT,
            "BillingService",
            CreateAuditPayload(
                request,
                paymentProvider.Key,
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
                    item.UnitPriceInCents)).ToArray());
            invoiceQueued = await invoiceEvents.PublishAsync(
                paymentEvent,
                // Nach erfolgreicher Belastung muss die Rechnung auch dann
                // eingeplant werden, wenn der ursprüngliche Client abbricht.
                CancellationToken.None);

            await audit.PublishAsync(
                AuditEventType.INVOICE,
                "BillingService",
                new {
                    phase = invoiceQueued ? "INVOICE_QUEUED" : "INVOICE_QUEUE_FAILED",
                    invoiceId,
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

    public override Task<PaymentProvidersResponse> ListPaymentProviders(
        PaymentProvidersRequest request,
        ServerCallContext context) {

        var response = new PaymentProvidersResponse();
        response.Providers.AddRange(paymentProviders.Providers.Select(provider =>
            new PaymentProviderInfo {
                Key = provider.Key,
                Name = provider.Name,
                IsTestMode = provider.IsTestMode
            }));

        logger.Debug(
            "Payment provider list requested.",
            new {
                providerCount = response.Providers.Count,
                providers = response.Providers.Select(provider => provider.Key).ToArray()
            });
        return Task.FromResult(response);
    }

    public override Task<BillingStatusResponse> GetStatus(
        BillingStatusRequest request,
        ServerCallContext context) {

        return Task.FromResult(new BillingStatusResponse {
            Available = true,
            Service = "BillingService"
        });
    }

    private static object CreateAuditPayload(
        PaymentRequest request,
        string providerKey,
        IPaymentProvider? provider,
        bool success,
        string? transactionId,
        string message,
        string? phase = null) => new {
            phase = phase ?? (success ? "PAYMENT_COMPLETED" : "PAYMENT_FAILED"),
            request.AmountInCents,
            request.Currency,
            paymentMethodSupplied = !string.IsNullOrWhiteSpace(request.PaymentMethod),
            request.Reference,
            providerKey,
            provider = provider?.Name,
            isTestMode = provider?.IsTestMode,
            customerEmailSupplied = !string.IsNullOrWhiteSpace(request.CustomerEmail),
            recipientDomain = GetRecipientDomain(request.CustomerEmail),
            invoiceItemCount = request.InvoiceItems.Count,
            success,
            transactionId,
            message
        };

    private static bool IsPaymentRequestValid(PaymentRequest request) {
        if (request.AmountInCents <= 0
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
}
