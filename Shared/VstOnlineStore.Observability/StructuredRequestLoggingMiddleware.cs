using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace VstOnlineStore.Observability;

/// <summary>
/// Erzeugt für jeden eingehenden HTTP- oder gRPC-Aufruf genau einen
/// strukturierten Abschluss-Eintrag. Die Correlation ID stammt aus dem
/// umgebenden CorrelationIdMiddleware-Scope.
/// </summary>
public sealed class StructuredRequestLoggingMiddleware(
    RequestDelegate next,
    IStructuredLogger logger) {

    public async Task InvokeAsync(HttpContext context) {
        var stopwatch = Stopwatch.StartNew();

        try {
            await next(context);
            stopwatch.Stop();

            logger.Info(
                "Request completed.",
                new {
                    httpMethod = context.Request.Method,
                    path = context.Request.Path.Value ?? "/",
                    protocol = context.Request.Protocol,
                    statusCode = context.Response.StatusCode,
                    durationMs = stopwatch.Elapsed.TotalMilliseconds
                });
        }
        catch (Exception exception) {
            stopwatch.Stop();

            logger.Error(
                "Request failed.",
                new {
                    httpMethod = context.Request.Method,
                    path = context.Request.Path.Value ?? "/",
                    protocol = context.Request.Protocol,
                    durationMs = stopwatch.Elapsed.TotalMilliseconds,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);

            throw;
        }
    }
}

public static class StructuredRequestLoggingExtensions {
    public static IApplicationBuilder UseStructuredRequestLogging(
        this IApplicationBuilder application) =>
        application.UseMiddleware<StructuredRequestLoggingMiddleware>();
}
