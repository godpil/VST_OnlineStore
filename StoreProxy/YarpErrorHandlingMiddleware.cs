using VstOnlineStore.Observability;
using Yarp.ReverseProxy.Forwarder;

namespace StoreProxy;

/// <summary>
/// Übersetzt technische Weiterleitungsfehler in eine stabile JSON-Antwort und
/// protokolliert die internen YARP-Details zusammen mit der Correlation ID.
/// </summary>
public sealed class YarpErrorHandlingMiddleware(
    RequestDelegate next,
    IStructuredLogger logger) {

    public async Task InvokeAsync(HttpContext context) {
        await next(context);

        var errorFeature = context.GetForwarderErrorFeature();
        if (errorFeature is null || errorFeature.Error == ForwarderError.None) {
            return;
        }

        var proxyFeature = context.GetReverseProxyFeature();
        var statusCode = ResolveStatusCode(
            errorFeature.Error,
            context.Response.StatusCode,
            errorFeature.Exception);
        var correlationId = CorrelationId.TryGet(context, out var currentCorrelationId)
            ? currentCorrelationId
            : Guid.NewGuid();

        var logContext = new {
            httpMethod = context.Request.Method,
            path = context.Request.Path.Value ?? "/",
            statusCode,
            yarpError = errorFeature.Error.ToString(),
            routeId = proxyFeature.Route.Config.RouteId,
            clusterId = proxyFeature.Cluster.Config.ClusterId,
            destination = proxyFeature.ProxiedDestination?.Model.Config.Address,
            exceptionType = errorFeature.Exception?.GetType().FullName,
            exceptionMessage = errorFeature.Exception?.Message
        };
        if (statusCode < StatusCodes.Status500InternalServerError) {
            logger.Warn(
                "Proxy request rejected.",
                logContext,
                errorFeature.Exception);
        }
        else {
            logger.Error(
                "Proxy forwarding failed.",
                logContext,
                errorFeature.Exception);
        }

        if (context.Response.HasStarted) {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;

        await context.Response.WriteAsJsonAsync(
            new {
                status = statusCode,
                message = GetClientMessage(statusCode),
                correlationID = correlationId
            },
            CancellationToken.None);
    }

    private static int ResolveStatusCode(
        ForwarderError error,
        int currentStatusCode,
        Exception? exception) {

        var badRequest = FindException<BadHttpRequestException>(exception);
        if (badRequest is not null) {
            return badRequest.StatusCode;
        }

        return error switch {
            ForwarderError.RequestTimedOut or ForwarderError.UpgradeActivityTimeout =>
                StatusCodes.Status504GatewayTimeout,
            ForwarderError.NoAvailableDestinations =>
                StatusCodes.Status503ServiceUnavailable,
            _ when currentStatusCode >= StatusCodes.Status500InternalServerError =>
                currentStatusCode,
            _ => StatusCodes.Status502BadGateway
        };
    }

    private static TException? FindException<TException>(Exception? exception)
        where TException : Exception {

        if (exception is TException matchingException) {
            return matchingException;
        }

        if (exception is AggregateException aggregateException) {
            foreach (var innerException in aggregateException.InnerExceptions) {
                var match = FindException<TException>(innerException);
                if (match is not null) {
                    return match;
                }
            }
        }

        return exception?.InnerException is null
            ? null
            : FindException<TException>(exception.InnerException);
    }

    private static string GetClientMessage(int statusCode) => statusCode switch {
        StatusCodes.Status503ServiceUnavailable =>
            "ShopService is currently unavailable.",
        StatusCodes.Status504GatewayTimeout =>
            "ShopService did not respond in time.",
        StatusCodes.Status413PayloadTooLarge =>
            "The request body exceeds the maximum size of 65536 bytes.",
        _ => "The request could not be forwarded to ShopService."
    };
}
