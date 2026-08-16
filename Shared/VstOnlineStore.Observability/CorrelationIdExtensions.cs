using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace VstOnlineStore.Observability;

public static class CorrelationIdExtensions {
    public static IServiceCollection AddCorrelationIdPropagation(
        this IServiceCollection services) {

        services.AddHttpContextAccessor();
        services.AddTransient<CorrelationIdDelegatingHandler>();
        return services;
    }

    public static IApplicationBuilder UseCorrelationId(
        this IApplicationBuilder application) =>
        application.UseMiddleware<CorrelationIdMiddleware>();
}
