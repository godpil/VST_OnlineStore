using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BillingService.Payments;

public static class PaymentProviderRegistration {
    /// <summary>
    /// Registriert alle konkreten Adapter aus dem BillingService-Assembly.
    /// Ein neuer Adapter muss dadurch nur IPaymentProvider implementieren;
    /// Program.cs und die Fassade bleiben unverändert.
    /// </summary>
    public static IServiceCollection AddPaymentFacade(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        var providerTypes = typeof(IPaymentProvider).Assembly.DefinedTypes
            .Where(type =>
                type is { IsAbstract: false, IsInterface: false }
                && typeof(IPaymentProvider).IsAssignableFrom(type.AsType()))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type => type.AsType());

        foreach (var providerType in providerTypes) {
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton(typeof(IPaymentProvider), providerType));
        }

        services.AddSingleton<IPaymentFacade, PaymentFacade>();
        return services;
    }
}
