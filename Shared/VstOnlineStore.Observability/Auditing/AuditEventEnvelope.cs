using System.Text.Json;
using System.Text.Json.Serialization;

namespace VstOnlineStore.Observability.Auditing;

[JsonConverter(typeof(JsonStringEnumConverter<AuditEventType>))]
public enum AuditEventType {
    ORDER_STARTED,
    ORDER_VALIDATED,
    STOCK_RESERVATION,
    PAYMENT,
    STOCK_RELEASE,
    ORDER_COMPLETED,
    INVOICE
}

[JsonConverter(typeof(JsonStringEnumConverter<AuditStatusCode>))]
public enum AuditStatusCode {
    SUCCESS,
    FAILURE,
    COMPENSATING,
    COMPENSATED
}

/// <summary>
/// Prozessübergreifender Vertrag für einen unveränderlichen Audit-Snapshot.
/// Event-ID und Zeitstempel entstehen beim Publisher und erlauben dem
/// AuditService eine idempotente Verarbeitung erneut zugestellter Nachrichten.
/// </summary>
public sealed record AuditEventEnvelope(
    Guid EventId,
    Guid CorrelationId,
    AuditEventType EventType,
    string ResponsibleService,
    DateTime Timestamp,
    JsonElement Payload,
    string Actor,
    AuditStatusCode StatusCode);
