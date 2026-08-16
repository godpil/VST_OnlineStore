using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace VstOnlineStore.Observability;

/// <summary>
/// Übernimmt eine gültige Correlation ID oder erzeugt am aktuellen Einstieg
/// eine neue GUID. Die kanonische ID steht allen nachfolgenden Komponenten zur
/// Verfügung und wird auch an den Aufrufer zurückgegeben.
/// </summary>
public sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger) {

    public async Task InvokeAsync(HttpContext context) {
        var correlationId = ReadOrCreate(context.Request.Headers[CorrelationId.HeaderName]);
        var formattedCorrelationId = correlationId.ToString("D");

        context.Items[CorrelationId.HttpContextItemKey] = correlationId;
        context.TraceIdentifier = formattedCorrelationId;
        context.Request.Headers[CorrelationId.HeaderName] = formattedCorrelationId;
        context.Response.OnStarting(() => {
            context.Response.Headers[CorrelationId.HeaderName] = formattedCorrelationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> {
            ["CorrelationId"] = formattedCorrelationId
        })) {
            await next(context);
        }
    }

    private static Guid ReadOrCreate(StringValues headerValues) {
        if (headerValues.Count == 1
            && Guid.TryParse(headerValues[0], out var suppliedCorrelationId)) {
            return suppliedCorrelationId;
        }

        return Guid.NewGuid();
    }
}
