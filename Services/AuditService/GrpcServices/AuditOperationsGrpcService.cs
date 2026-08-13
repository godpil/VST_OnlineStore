using Grpc.Core;
using VstOnlineStore.Contracts.AuditService;

namespace AuditService.GrpcServices;

public sealed class AuditOperationsGrpcService : AuditOperations.AuditOperationsBase {
    public override Task<AuditStatusResponse> GetStatus(
        AuditStatusRequest request,
        ServerCallContext context) {

        return Task.FromResult(new AuditStatusResponse {
            Available = true,
            Service = "AuditService"
        });
    }
}
