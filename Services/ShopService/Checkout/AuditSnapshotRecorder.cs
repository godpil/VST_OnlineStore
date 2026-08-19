using VstOnlineStore.Observability.Auditing;
using AuditContracts = VstOnlineStore.Contracts.AuditService;
using SharedEventType = VstOnlineStore.Observability.Auditing.AuditEventType;
using SharedStatusCode = VstOnlineStore.Observability.Auditing.AuditStatusCode;

namespace ShopService.Checkout;

/// <summary>
/// Schreibt fachliche Order-Snapshots best effort. Eine nicht verfügbare
/// Audit-Senke darf Zahlung oder Kompensation nicht blockieren.
/// </summary>
public sealed class AuditSnapshotRecorder(
    IAuditEventPublisher audit) {

    public async Task RecordAsync(
        AuditContracts.AuditEventType eventType,
        string responsibleService,
        object payload,
        string actor,
        AuditContracts.AuditStatusCode statusCode,
        CancellationToken cancellationToken) {

        await audit.PublishAsync(
            ToSharedEventType(eventType),
            responsibleService,
            payload,
            actor,
            ToSharedStatusCode(statusCode),
            cancellationToken: cancellationToken);
    }

    private static SharedEventType ToSharedEventType(
        AuditContracts.AuditEventType eventType) => eventType switch {
            AuditContracts.AuditEventType.OrderStarted => SharedEventType.ORDER_STARTED,
            AuditContracts.AuditEventType.OrderValidated => SharedEventType.ORDER_VALIDATED,
            AuditContracts.AuditEventType.StockReservation => SharedEventType.STOCK_RESERVATION,
            AuditContracts.AuditEventType.Payment => SharedEventType.PAYMENT,
            AuditContracts.AuditEventType.StockRelease => SharedEventType.STOCK_RELEASE,
            AuditContracts.AuditEventType.OrderCompleted => SharedEventType.ORDER_COMPLETED,
            AuditContracts.AuditEventType.Invoice => SharedEventType.INVOICE,
            _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, null)
        };

    private static SharedStatusCode ToSharedStatusCode(
        AuditContracts.AuditStatusCode statusCode) => statusCode switch {
            AuditContracts.AuditStatusCode.Success => SharedStatusCode.SUCCESS,
            AuditContracts.AuditStatusCode.Failure => SharedStatusCode.FAILURE,
            AuditContracts.AuditStatusCode.Compensating => SharedStatusCode.COMPENSATING,
            AuditContracts.AuditStatusCode.Compensated => SharedStatusCode.COMPENSATED,
            _ => throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, null)
        };
}
