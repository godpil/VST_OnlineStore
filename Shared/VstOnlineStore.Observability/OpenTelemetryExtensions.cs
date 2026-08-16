using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace VstOnlineStore.Observability;

public static class OpenTelemetryExtensions {
    public static IServiceCollection AddVstOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName) {

        var configuredEndpoint = configuration["OpenTelemetry:OtlpEndpoint"]
            ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
            ?? "http://localhost:6687";

        if (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var endpoint)) {
            throw new InvalidOperationException(
                $"Der OpenTelemetry-Endpunkt '{configuredEndpoint}' ist ungültig.");
        }

        var logRootDirectory = ResolveLogRootDirectory(configuration);
        // Das Projekt schreibt bewusst in Debug-Konsole, JSONL und OTLP. Der
        // Windows-EventLog-Provider benötigt dagegen eine registrierte Quelle
        // beziehungsweise erhöhte Rechte und darf Anwendungsaufrufe nicht
        // durch eine fehlende Betriebssystemberechtigung abbrechen.
        if (OperatingSystem.IsWindows()) {
            var eventLogRegistrations = services
                .Where(registration =>
                    registration.ServiceType == typeof(ILoggerProvider)
                    && registration.ImplementationType == typeof(EventLogLoggerProvider))
                .ToArray();
            foreach (var registration in eventLogRegistrations) {
                services.Remove(registration);
            }
        }
        services.AddHttpContextAccessor();
        services.AddSingleton(new StructuredLoggingOptions(
            serviceName,
            logRootDirectory,
            RetentionDays: 14));
        services.AddSingleton<DailyJsonLogFileSink>();
        services.AddSingleton<IStructuredLogger, StructuredLogger>();
        services.Configure<LoggerFilterOptions>(filtering => {
            filtering.Rules.Add(new LoggerFilterRule(
                providerName: null,
                categoryName: typeof(StructuredLogger).FullName,
                logLevel: LogLevel.Debug,
                filter: null));
        });

        services.Configure<OpenTelemetryLoggerOptions>(logging => {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.ParseStateValues = true;
        });

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: serviceName,
                serviceInstanceId: $"{Environment.MachineName}:{Environment.ProcessId}"))
            .WithLogging(logging => {
                logging.AddOtlpExporter(exporter => {
                    exporter.Endpoint = endpoint;
                    exporter.Protocol = OtlpExportProtocol.Grpc;
                });
            });

        return services;
    }

    private static string ResolveLogRootDirectory(IConfiguration configuration) {
        var configuredDirectory = configuration["StructuredLogging:LogDirectory"]
            ?? Environment.GetEnvironmentVariable("VST_STRUCTURED_LOG_DIRECTORY");

        if (!string.IsNullOrWhiteSpace(configuredDirectory)) {
            return Path.GetFullPath(configuredDirectory);
        }

        var currentDirectory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var directory = currentDirectory; directory is not null; directory = directory.Parent) {
            if (File.Exists(Path.Combine(directory.FullName, "VST_OnlineStore.slnx"))) {
                return Path.Combine(directory.FullName, "Logs");
            }
        }

        return Path.Combine(currentDirectory.FullName, "Logs");
    }
}
