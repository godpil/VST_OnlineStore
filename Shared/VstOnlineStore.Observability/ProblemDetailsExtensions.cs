using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace VstOnlineStore.Observability;

/// <summary>
/// Konfiguriert RFC-9457-Problem-Details einheitlich für alle öffentlichen
/// HTTP-Grenzen des Stores.
/// </summary>
public static class ProblemDetailsExtensions {
    public static IServiceCollection AddVstProblemDetails(
        this IServiceCollection services) {

        services.AddProblemDetails(options => {
            options.CustomizeProblemDetails = context => {
                if (!CorrelationId.TryGet(context.HttpContext, out var correlationId)) {
                    return;
                }

                var formattedCorrelationId = correlationId.ToString("D");
                context.ProblemDetails.Instance ??= $"urn:uuid:{formattedCorrelationId}";
                context.ProblemDetails.Extensions.TryAdd(
                    "correlationID",
                    formattedCorrelationId);
            };
        });

        return services;
    }
}
