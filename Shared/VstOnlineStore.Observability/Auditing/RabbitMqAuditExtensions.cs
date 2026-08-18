using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace VstOnlineStore.Observability.Auditing;

public static class RabbitMqAuditExtensions {
    public static IServiceCollection AddRabbitMqAuditPublishing(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName) {

        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        services.AddHttpContextAccessor();
        services.Configure<RabbitMqAuditOptions>(
            configuration.GetSection(RabbitMqAuditOptions.SectionName));
        services.AddSingleton(new RabbitMqPublisherIdentity(serviceName.Trim()));
        services.AddSingleton<IAuditEventPublisher, RabbitMqAuditEventPublisher>();
        return services;
    }
}
