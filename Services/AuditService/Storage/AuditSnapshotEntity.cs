using System.Text.Json;
using AuditService.Domain;

namespace AuditService.Storage;

internal sealed class AuditSnapshotEntity {
    public Guid EventId { get; set; }
    public Guid CorrelationId { get; set; }
    public AuditEventType EventType { get; set; }
    public required string ResponsibleService { get; set; }
    public DateTime Timestamp { get; set; }
    public JsonElement Payload { get; set; }
    public Guid? PreviousEventId { get; set; }
    public required string Actor { get; set; }
    public AuditStatusCode StatusCode { get; set; }
    public long SequenceNumber { get; set; }

    public AuditSnapshot ToDomain() => new(
        EventId,
        CorrelationId,
        EventType,
        ResponsibleService,
        Timestamp,
        Payload.Clone(),
        PreviousEventId,
        Actor,
        StatusCode,
        SequenceNumber);

    public static AuditSnapshotEntity FromDomain(AuditSnapshot snapshot) => new() {
        EventId = snapshot.EventId,
        CorrelationId = snapshot.CorrelationId,
        EventType = snapshot.EventType,
        ResponsibleService = snapshot.ResponsibleService,
        Timestamp = snapshot.Timestamp,
        Payload = snapshot.Payload.Clone(),
        PreviousEventId = snapshot.PreviousEventId,
        Actor = snapshot.Actor,
        StatusCode = snapshot.StatusCode,
        SequenceNumber = snapshot.SequenceNumber
    };
}
