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
        await Results.Problem(
                detail: GetClientMessage(statusCode),
                statusCode: statusCode)
            .ExecuteAsync(context);
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
            "Der ShopService ist derzeit nicht erreichbar.",
        StatusCodes.Status504GatewayTimeout =>
            "Der ShopService hat nicht rechtzeitig geantwortet.",
        StatusCodes.Status413PayloadTooLarge =>
            "Der Request-Body überschreitet die maximale Größe von 65536 Bytes.",
        _ => "Die Anfrage konnte nicht an den ShopService weitergeleitet werden."
    };
}
