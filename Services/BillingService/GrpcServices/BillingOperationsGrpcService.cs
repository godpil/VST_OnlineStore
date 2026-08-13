using Grpc.Core;
using VstOnlineStore.Contracts.BillingService;

namespace BillingService.GrpcServices;

public sealed class BillingOperationsGrpcService : BillingOperations.BillingOperationsBase {
    public override Task<BillingStatusResponse> GetStatus(
        BillingStatusRequest request,
        ServerCallContext context) {

        return Task.FromResult(new BillingStatusResponse {
            Available = true,
            Service = "BillingService"
        });
    }
}
