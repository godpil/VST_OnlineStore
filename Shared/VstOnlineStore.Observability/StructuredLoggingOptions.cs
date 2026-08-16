namespace VstOnlineStore.Observability;

internal sealed record StructuredLoggingOptions(
    string ServiceName,
    string LogRootDirectory,
    int RetentionDays);
