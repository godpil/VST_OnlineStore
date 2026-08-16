namespace VstOnlineStore.Observability;

/// <summary>
/// Unterstützte Schweregrade eines strukturierten Anwendungslogs.
/// Die Bezeichner entsprechen zugleich den Werten im JSON-Dokument.
/// </summary>
public enum StructuredLogLevel {
    INFO,
    WARN,
    ERROR,
    DEBUG
}
