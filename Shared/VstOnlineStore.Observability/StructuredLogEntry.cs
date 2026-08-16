using System.Text.Json;
using System.Text.Json.Serialization;

namespace VstOnlineStore.Observability;

/// <summary>
/// Einheitlicher JSON-Vertrag für technische Logs aller Services.
/// </summary>
public sealed record StructuredLogEntry(
    [property: JsonPropertyName("timeStamp")] DateTime TimeStamp,
    [property: JsonPropertyName("correlationID")] Guid CorrelationId,
    [property: JsonPropertyName("serviceName")] string ServiceName,
    [property: JsonPropertyName("logLevel")]
    [property: JsonConverter(typeof(JsonStringEnumConverter<StructuredLogLevel>))]
    StructuredLogLevel LogLevel,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("context")] JsonElement Context);
