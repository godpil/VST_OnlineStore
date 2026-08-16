namespace VstOnlineStore.Observability;

/// <summary>
/// Gemeinsamer Einstiegspunkt für strukturierte Logs. Der Kontext darf ein
/// beliebiges JSON-serialisierbares Objekt sein.
/// </summary>
public interface IStructuredLogger {
    void Log(
        StructuredLogLevel logLevel,
        string message,
        object? context = null,
        Exception? exception = null);
}

public static class StructuredLoggerExtensions {
    public static void Info(
        this IStructuredLogger logger,
        string message,
        object? context = null) =>
        logger.Log(StructuredLogLevel.INFO, message, context);

    public static void Warn(
        this IStructuredLogger logger,
        string message,
        object? context = null,
        Exception? exception = null) =>
        logger.Log(StructuredLogLevel.WARN, message, context, exception);

    public static void Error(
        this IStructuredLogger logger,
        string message,
        object? context = null,
        Exception? exception = null) =>
        logger.Log(StructuredLogLevel.ERROR, message, context, exception);

    public static void Debug(
        this IStructuredLogger logger,
        string message,
        object? context = null) =>
        logger.Log(StructuredLogLevel.DEBUG, message, context);
}
