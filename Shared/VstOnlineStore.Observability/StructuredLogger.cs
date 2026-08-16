using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace VstOnlineStore.Observability;

internal sealed class StructuredLogger(
    ILogger<StructuredLogger> logger,
    IHttpContextAccessor httpContextAccessor,
    StructuredLoggingOptions options,
    DailyJsonLogFileSink fileSink) : IStructuredLogger {

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void Log(
        StructuredLogLevel logLevel,
        string message,
        object? context = null,
        Exception? exception = null) {

        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var timestamp = DateTime.UtcNow;
        var correlationId = GetCorrelationId();
        var entry = new StructuredLogEntry(
            timestamp,
            correlationId,
            options.ServiceName,
            logLevel,
            message,
            SerializeContext(context));
        var json = JsonSerializer.Serialize(entry, JsonOptions);

        // Die Tagesdatei ist die lokale, flache JSONL-Darstellung. Fehler in
        // einer Logsenke dürfen die Fachfunktion des aufrufenden Services
        // nicht abbrechen.
        try {
            fileSink.Write(timestamp, json);
        }
        catch (Exception sinkException) when (sinkException is IOException
            or UnauthorizedAccessException
            or ObjectDisposedException) {
            System.Diagnostics.Debug.WriteLine(
                $"Structured log file sink failed: {sinkException.Message}");
        }

        var state = new StructuredLogState(entry, json);
        try {
            logger.Log(
                MapLogLevel(logLevel),
                new EventId(2000, "StructuredLog"),
                state,
                exception,
                static (logState, _) => logState.Json);
        }
        catch (Exception sinkException) {
            // Kein externer Logging-Provider darf einen fachlichen Aufruf oder
            // eine bereits vorbereitete HTTP-Antwort abbrechen.
            System.Diagnostics.Debug.WriteLine(
                $"Structured logging provider failed: {sinkException.Message}");
        }
    }

    private Guid GetCorrelationId() {
        var httpContext = httpContextAccessor.HttpContext;
        return httpContext is not null
            && CorrelationId.TryGet(httpContext, out var correlationId)
                ? correlationId
                : Guid.NewGuid();
    }

    private static JsonElement SerializeContext(object? context) {
        if (context is null) {
            return JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>(),
                JsonOptions);
        }

        var element = context is JsonElement jsonElement
            ? jsonElement.Clone()
            : JsonSerializer.SerializeToElement(context, context.GetType(), JsonOptions);

        return element.ValueKind == JsonValueKind.Object
            ? element
            : JsonSerializer.SerializeToElement(
                new Dictionary<string, JsonElement> { ["value"] = element },
                JsonOptions);
    }

    private static LogLevel MapLogLevel(StructuredLogLevel logLevel) => logLevel switch {
        StructuredLogLevel.DEBUG => LogLevel.Debug,
        StructuredLogLevel.INFO => LogLevel.Information,
        StructuredLogLevel.WARN => LogLevel.Warning,
        StructuredLogLevel.ERROR => LogLevel.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(logLevel), logLevel, null)
    };

    private sealed class StructuredLogState(
        StructuredLogEntry entry,
        string json) : IReadOnlyList<KeyValuePair<string, object?>> {

        private readonly KeyValuePair<string, object?>[] _attributes = [
            new("timeStamp", entry.TimeStamp.ToString("O")),
            new("correlationID", entry.CorrelationId.ToString("D")),
            new("serviceName", entry.ServiceName),
            new("logLevel", entry.LogLevel.ToString()),
            new("message", entry.Message),
            new("context", entry.Context.GetRawText()),
            new("structuredLog", true)
        ];

        public string Json { get; } = json;

        public int Count => _attributes.Length;

        public KeyValuePair<string, object?> this[int index] => _attributes[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, object?>>)_attributes).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
