using Microsoft.AspNetCore.Http;

namespace VstOnlineStore.Observability;

/// <summary>
/// Reicht die Correlation ID des aktuellen Vorgangs an ausgehende HTTP- und
/// gRPC-Aufrufe weiter.
/// </summary>
public sealed class CorrelationIdDelegatingHandler(
    IHttpContextAccessor httpContextAccessor) : DelegatingHandler {

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) {

        var correlationId = GetCurrentOrCreate();
        request.Headers.Remove(CorrelationId.HeaderName);
        request.Headers.TryAddWithoutValidation(
            CorrelationId.HeaderName,
            correlationId.ToString("D"));

        return base.SendAsync(request, cancellationToken);
    }

    private Guid GetCurrentOrCreate() {
        var context = httpContextAccessor.HttpContext;
        if (context is not null && CorrelationId.TryGet(context, out var correlationId)) {
            return correlationId;
        }

        return Guid.NewGuid();
    }
}
