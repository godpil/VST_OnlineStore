using System.Text.Json;
using AuditService.Application;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using AuditContracts = VstOnlineStore.Contracts.AuditService;
using DomainEventType = AuditService.Domain.AuditEventType;
using DomainStatusCode = AuditService.Domain.AuditStatusCode;

namespace AuditService.GrpcServices;

public sealed class AuditOperationsGrpcService(
    AuditApplicationService applicationService) : AuditContracts.AuditOperations.AuditOperationsBase {

    public override async Task<AuditContracts.RecordAuditSnapshotResponse> RecordSnapshot(
        AuditContracts.RecordAuditSnapshotRequest request,
        ServerCallContext context) {

        try {
            if (!Guid.TryParse(request.CorrelationId, out var correlationId)
                || correlationId == Guid.Empty) {
                throw new ArgumentException("Die Correlation-ID ist ungültig.");
            }

            using var payloadDocument = JsonDocument.Parse(request.PayloadJson);
            var snapshot = await applicationService.RecordAsync(
                correlationId,
                ToDomainEventType(request.EventType),
                request.ResponsibleService,
                payloadDocument.RootElement,
                request.Actor,
                ToDomainStatusCode(request.StatusCode),
                context.CancellationToken);

            return new AuditContracts.RecordAuditSnapshotResponse {
                Snapshot = ToContract(snapshot)
            };
        }
        catch (Exception exception) when (exception is ArgumentException
            or ArgumentOutOfRangeException
            or JsonException) {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                exception.Message));
        }
    }

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

    private static DomainEventType ToDomainEventType(
        AuditContracts.AuditEventType eventType) => eventType switch {
            AuditContracts.AuditEventType.OrderStarted => DomainEventType.ORDER_STARTED,
            AuditContracts.AuditEventType.OrderValidated => DomainEventType.ORDER_VALIDATED,
            AuditContracts.AuditEventType.StockReservation => DomainEventType.STOCK_RESERVATION,
            AuditContracts.AuditEventType.Payment => DomainEventType.PAYMENT,
            AuditContracts.AuditEventType.StockRelease => DomainEventType.STOCK_RELEASE,
            AuditContracts.AuditEventType.OrderCompleted => DomainEventType.ORDER_COMPLETED,
            _ => throw new ArgumentOutOfRangeException(
                nameof(eventType),
                eventType,
                "Der Event-Typ ist nicht spezifiziert.")
        };

    private static DomainStatusCode ToDomainStatusCode(
        AuditContracts.AuditStatusCode statusCode) => statusCode switch {
            AuditContracts.AuditStatusCode.Success => DomainStatusCode.SUCCESS,
            AuditContracts.AuditStatusCode.Failure => DomainStatusCode.FAILURE,
            AuditContracts.AuditStatusCode.Compensating => DomainStatusCode.COMPENSATING,
            AuditContracts.AuditStatusCode.Compensated => DomainStatusCode.COMPENSATED,
            _ => throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                statusCode,
                "Der Audit-Status ist nicht spezifiziert.")
        };

    private static AuditContracts.AuditEventType ToContractEventType(
        DomainEventType eventType) => eventType switch {
            DomainEventType.ORDER_STARTED => AuditContracts.AuditEventType.OrderStarted,
            DomainEventType.ORDER_VALIDATED => AuditContracts.AuditEventType.OrderValidated,
            DomainEventType.STOCK_RESERVATION => AuditContracts.AuditEventType.StockReservation,
            DomainEventType.PAYMENT => AuditContracts.AuditEventType.Payment,
            DomainEventType.STOCK_RELEASE => AuditContracts.AuditEventType.StockRelease,
            DomainEventType.ORDER_COMPLETED => AuditContracts.AuditEventType.OrderCompleted,
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
