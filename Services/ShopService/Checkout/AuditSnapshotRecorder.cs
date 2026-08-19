using VstOnlineStore.Observability.Auditing;

namespace ShopService.Checkout;

/// <summary>
/// Schreibt fachliche Order-Snapshots best effort. Eine nicht verfügbare
/// Audit-Senke darf Zahlung oder Kompensation nicht blockieren.
/// </summary>
public sealed class AuditSnapshotRecorder(
    IAuditEventPublisher audit) {

    public Task RecordAsync(
        AuditEventType eventType,
        string responsibleService,
        object payload,
        string actor,
        AuditStatusCode statusCode,
        CancellationToken cancellationToken) =>
        audit.PublishAsync(
            eventType,
            responsibleService,
            payload,
            actor,
            statusCode,
            cancellationToken: cancellationToken);
}
