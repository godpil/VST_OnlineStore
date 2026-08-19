using BillingService.Payments;
using Grpc.Core;
using VstOnlineStore.Contracts.BillingService;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;

namespace BillingService.GrpcServices;

public sealed class BillingOperationsGrpcService(
    PaymentProviderResolver paymentProviders,
    IAuditEventPublisher audit,
    IStructuredLogger logger) : BillingOperations.BillingOperationsBase {

    public override async Task<PaymentResponse> ProcessPayment(
        PaymentRequest request,
        ServerCallContext context) {

        var requestedProviderKey = string.IsNullOrWhiteSpace(request.PaymentProvider)
            ? "demo"
            : request.PaymentProvider.Trim();

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

        if (request.AmountInCents <= 0 || string.IsNullOrWhiteSpace(request.Currency)) {
            const string message = "Betrag und Währung müssen gültig sein.";
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
            result = await paymentProvider.ChargeAsync(
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

        return new PaymentResponse {
            Success = result.Success,
            TransactionId = result.TransactionId,
            Provider = paymentProvider.Name,
            Message = result.Message
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
            success,
            transactionId,
            message
        };
}
