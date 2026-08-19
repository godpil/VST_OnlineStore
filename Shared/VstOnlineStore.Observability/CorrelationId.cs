using Microsoft.AspNetCore.Http;

namespace VstOnlineStore.Observability;

/// <summary>
/// Gemeinsame Konventionen für die Correlation ID eines Vorgangs.
/// </summary>
public static class CorrelationId {
    public const string HeaderName = "X-Correlation-ID";

    internal static readonly object HttpContextItemKey = new();
    private static readonly AsyncLocal<Guid?> AmbientCorrelationId = new();

    public static bool TryGet(HttpContext context, out Guid correlationId) {
        if (context.Items.TryGetValue(HttpContextItemKey, out var value)
            && value is Guid storedCorrelationId) {
            correlationId = storedCorrelationId;
            return true;
        }

        correlationId = default;
        return false;
    }

    /// <summary>
    /// Stellt die Correlation-ID auch außerhalb eines HTTP-Kontexts bereit,
    /// beispielsweise während der Verarbeitung einer RabbitMQ-Nachricht.
    /// </summary>
    public static IDisposable BeginScope(Guid correlationId) {
        if (correlationId == Guid.Empty) {
            throw new ArgumentException(
                "Die Correlation-ID darf nicht leer sein.",
                nameof(correlationId));
        }

        var previousCorrelationId = AmbientCorrelationId.Value;
        AmbientCorrelationId.Value = correlationId;
        return new CorrelationScope(previousCorrelationId);
    }

    internal static bool TryGetAmbient(out Guid correlationId) {
        if (AmbientCorrelationId.Value is Guid current && current != Guid.Empty) {
            correlationId = current;
            return true;
        }

        correlationId = default;
        return false;
    }

    private sealed class CorrelationScope(Guid? previousCorrelationId) : IDisposable {
        private bool _disposed;

        public void Dispose() {
            if (_disposed) {
                return;
            }

            AmbientCorrelationId.Value = previousCorrelationId;
            _disposed = true;
        }
    }
}
