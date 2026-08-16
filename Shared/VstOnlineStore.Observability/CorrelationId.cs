using Microsoft.AspNetCore.Http;

namespace VstOnlineStore.Observability;

/// <summary>
/// Gemeinsame Konventionen für die Correlation ID eines Vorgangs.
/// </summary>
public static class CorrelationId {
    public const string HeaderName = "X-Correlation-ID";

    internal static readonly object HttpContextItemKey = new();

    public static bool TryGet(HttpContext context, out Guid correlationId) {
        if (context.Items.TryGetValue(HttpContextItemKey, out var value)
            && value is Guid storedCorrelationId) {
            correlationId = storedCorrelationId;
            return true;
        }

        correlationId = default;
        return false;
    }
}
