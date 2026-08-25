namespace VstOnlineStore.Presentation;

/// <summary>
/// Deterministische Fehlerszenarien für Vorführungen. Die Werte werden über
/// REST, gRPC und RabbitMQ weitergereicht und gelten immer nur für eine
/// einzelne Bestellung.
/// </summary>
public static class PresentationScenarios {
    public const string PaymentDeclined = "payment-declined";
    public const string OutOfStock = "out-of-stock";
    public const string InvoiceServiceUnavailable = "invoice-service-unavailable";
    public const string WarehouseCommitFailed = "warehouse-commit-failed";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [
            PaymentDeclined,
            OutOfStock,
            InvoiceServiceUnavailable,
            WarehouseCommitFailed
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string? scenario) =>
        !string.IsNullOrWhiteSpace(scenario) && All.Contains(scenario.Trim());

    public static bool Is(string? actual, string expected) =>
        string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}

public sealed class PresentationModeOptions {
    public const string SectionName = "PresentationMode";

    public bool Enabled { get; set; }
}
