using BillingService.Payments;
using Grpc.Core;
using VstOnlineStore.Contracts.BillingService;

namespace BillingService.GrpcServices;

public sealed class BillingOperationsGrpcService(
    IPaymentProvider paymentProvider) : BillingOperations.BillingOperationsBase {

    public override async Task<PaymentResponse> ProcessPayment(
        PaymentRequest request,
        ServerCallContext context) {

        if (request.AmountInCents <= 0 || string.IsNullOrWhiteSpace(request.Currency)) {
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
}
