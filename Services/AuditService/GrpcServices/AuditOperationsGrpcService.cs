using AuditService.Application;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using AuditContracts = VstOnlineStore.Contracts.AuditService;
using DomainEventType = AuditService.Domain.AuditEventType;
using DomainStatusCode = AuditService.Domain.AuditStatusCode;

namespace AuditService.GrpcServices;

public sealed class AuditOperationsGrpcService(
    AuditApplicationService applicationService) : AuditContracts.AuditOperations.AuditOperationsBase {

    public override async Task<AuditContracts.GetOrderSnapshotsResponse> GetOrderSnapshots(
        AuditContracts.GetOrderSnapshotsRequest request,
        ServerCallContext context) {

        if (!Guid.TryParse(request.CorrelationId, out var correlationId)
            || correlationId == Guid.Empty) {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "Die Correlation-ID ist ungültig."));
        }

        var snapshots = await applicationService.GetOrderSnapshotsAsync(
            correlationId,
            context.CancellationToken);
        var response = new AuditContracts.GetOrderSnapshotsResponse();
        response.Snapshots.AddRange(snapshots.Select(ToContract));
        return response;
    }

    public override Task<AuditContracts.AuditStatusResponse> GetStatus(
        AuditContracts.AuditStatusRequest request,
        ServerCallContext context) {

        return Task.FromResult(new AuditContracts.AuditStatusResponse {
            Available = true,
            Service = "AuditService"
        });
    }

    private static AuditContracts.AuditSnapshot ToContract(
        Domain.AuditSnapshot snapshot) =>
        new() {
            EventId = snapshot.EventId.ToString("D"),
            CorrelationId = snapshot.CorrelationId.ToString("D"),
            EventType = ToContractEventType(snapshot.EventType),
            ResponsibleService = snapshot.ResponsibleService,
            Timestamp = Timestamp.FromDateTime(snapshot.Timestamp),
            PayloadJson = snapshot.Payload.GetRawText(),
            PreviousEventId = snapshot.PreviousEventId?.ToString("D") ?? string.Empty,
            Actor = snapshot.Actor,
            StatusCode = ToContractStatusCode(snapshot.StatusCode),
            SequenceNumber = snapshot.SequenceNumber
        };

    private static AuditContracts.AuditEventType ToContractEventType(
        DomainEventType eventType) => eventType switch {
            DomainEventType.ORDER_STARTED => AuditContracts.AuditEventType.OrderStarted,
            DomainEventType.ORDER_VALIDATED => AuditContracts.AuditEventType.OrderValidated,
            DomainEventType.STOCK_RESERVATION => AuditContracts.AuditEventType.StockReservation,
            DomainEventType.PAYMENT => AuditContracts.AuditEventType.Payment,
            DomainEventType.STOCK_RELEASE => AuditContracts.AuditEventType.StockRelease,
            DomainEventType.ORDER_COMPLETED => AuditContracts.AuditEventType.OrderCompleted,
            DomainEventType.INVOICE => AuditContracts.AuditEventType.Invoice,
            _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, null)
        };

    private static AuditContracts.AuditStatusCode ToContractStatusCode(
        DomainStatusCode statusCode) => statusCode switch {
            DomainStatusCode.SUCCESS => AuditContracts.AuditStatusCode.Success,
            DomainStatusCode.FAILURE => AuditContracts.AuditStatusCode.Failure,
            DomainStatusCode.COMPENSATING => AuditContracts.AuditStatusCode.Compensating,
            DomainStatusCode.COMPENSATED => AuditContracts.AuditStatusCode.Compensated,
            _ => throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, null)
        };
}
