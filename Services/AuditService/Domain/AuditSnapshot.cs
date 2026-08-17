using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuditService.Domain;

public enum AuditEventType {
    ORDER_STARTED,
    ORDER_VALIDATED,
    STOCK_RESERVATION,
    PAYMENT,
    STOCK_RELEASE,
    ORDER_COMPLETED
}

public enum AuditStatusCode {
    SUCCESS,
    FAILURE,
    COMPENSATING,
    COMPENSATED
}

public sealed record AuditSnapshot(
    [property: JsonPropertyName("eventID")] Guid EventId,
    [property: JsonPropertyName("correlationID")] Guid CorrelationId,
    [property: JsonPropertyName("eventType")] AuditEventType EventType,
    [property: JsonPropertyName("responsibleService")] string ResponsibleService,
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("previousEventID")] Guid? PreviousEventId,
    [property: JsonPropertyName("actor")] string Actor,
    [property: JsonPropertyName("statusCode")] AuditStatusCode StatusCode,
    [property: JsonPropertyName("sequenceNumber")] long SequenceNumber);

public sealed record AuditSnapshotDraft(
    Guid CorrelationId,
    AuditEventType EventType,
    string ResponsibleService,
    JsonElement Payload,
    string Actor,
    AuditStatusCode StatusCode);
