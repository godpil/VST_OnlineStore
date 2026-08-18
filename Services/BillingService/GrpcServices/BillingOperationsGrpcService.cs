using BillingService.Payments;
using Grpc.Core;
using VstOnlineStore.Contracts.BillingService;
using VstOnlineStore.Observability.Auditing;

namespace BillingService.GrpcServices;

public sealed class BillingOperationsGrpcService(
    IPaymentProvider paymentProvider,
    IAuditEventPublisher audit) : BillingOperations.BillingOperationsBase {

    public override async Task<PaymentResponse> ProcessPayment(
        PaymentRequest request,
        ServerCallContext context) {

        if (request.AmountInCents <= 0 || string.IsNullOrWhiteSpace(request.Currency)) {
            await audit.PublishAsync(
                AuditEventType.PAYMENT,
                "BillingService",
                CreateAuditPayload(request, false, null, "Betrag und Währung müssen gültig sein."),
                paymentProvider.Name,
                AuditStatusCode.FAILURE,
                cancellationToken: context.CancellationToken);
            return new PaymentResponse {
                Success = false,
                Provider = paymentProvider.Name,
                Message = "Betrag und Währung müssen gültig sein."
            };
        }

        var result = await paymentProvider.ChargeAsync(
            request.AmountInCents,
            request.Currency,
            request.PaymentMethod,
            request.Reference,
            context.CancellationToken);

        await audit.PublishAsync(
            AuditEventType.PAYMENT,
            "BillingService",
            CreateAuditPayload(
                request,
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

    public override Task<BillingStatusResponse> GetStatus(
        BillingStatusRequest request,
        ServerCallContext context) {

        return Task.FromResult(new BillingStatusResponse {
            Available = true,
            Service = "BillingService"
        });
    }

    private object CreateAuditPayload(
        PaymentRequest request,
        bool success,
        string? transactionId,
        string message) => new {
            phase = success ? "PAYMENT_COMPLETED" : "PAYMENT_FAILED",
            request.AmountInCents,
            request.Currency,
            request.PaymentMethod,
            request.Reference,
            provider = paymentProvider.Name,
            success,
            transactionId,
            message
        };
}
