using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShopService.Api;

public sealed record ProductResponse(
    string Id,
    string Name,
    decimal Price,
    string Image,
    int AvailableQuantity,
    bool IsSoldOut);

public sealed record PaymentProviderResponse(
    string Key,
    string Name,
    bool IsTestMode);

public sealed record OrderAuditSnapshotResponse(
    [property: JsonPropertyName("eventID")] Guid EventId,
    [property: JsonPropertyName("correlationID")] Guid CorrelationId,
    string EventType,
    string ResponsibleService,
    DateTime Timestamp,
    JsonElement Payload,
    [property: JsonPropertyName("previousEventID")] Guid? PreviousEventId,
    string Actor,
    string StatusCode);

public sealed record HealthResponse(
    string Status,
    string Service);
