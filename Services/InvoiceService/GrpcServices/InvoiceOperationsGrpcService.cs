using Grpc.Core;
using VstOnlineStore.Contracts.InvoiceService;

namespace InvoiceService.GrpcServices;

public sealed class InvoiceOperationsGrpcService : InvoiceOperations.InvoiceOperationsBase {
    public override Task<InvoiceStatusResponse> GetStatus(
        InvoiceStatusRequest request,
        ServerCallContext context) {

        return Task.FromResult(new InvoiceStatusResponse {
            Available = true,
            Service = "InvoiceService"
        });
    }
}
