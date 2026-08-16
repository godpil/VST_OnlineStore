using VstOnlineStore.Observability;

namespace StoreProxy {
    public class Program {
        public static void Main(string[] args) {
            var builder =
                WebApplication.CreateBuilder(args);

            builder.Services
                .AddReverseProxy()
                .LoadFromConfig(
                    builder.Configuration
                        .GetSection("ReverseProxy"));
            builder.Services.AddVstOpenTelemetry(
                builder.Configuration,
                "StoreProxy");

            var app = builder.Build();

            app.UseCorrelationId();
            app.UseStructuredRequestLogging();
            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.MapReverseProxy();

            app.Run();

        }
    }
}
