namespace ShopService.Orchestration;

/// <summary>
/// Begrenzt alle synchronen Abhaengigkeitsaufrufe des ShopService. Damit
/// blockiert weder die Betriebszustandspruefung noch eine laufende Saga
/// unbegrenzt auf einen nicht antwortenden Service.
/// </summary>
public sealed class ShopServiceTimeoutOptions {
    public const string SectionName = "DownstreamTimeouts";

    public TimeSpan StatusProbe { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan CatalogQuery { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan StockOperation { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan PaymentOperation { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan Compensation { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsValid() =>
        StatusProbe > TimeSpan.Zero &&
        CatalogQuery > TimeSpan.Zero &&
        StockOperation > TimeSpan.Zero &&
        PaymentOperation > TimeSpan.Zero &&
        Compensation > TimeSpan.Zero;
}
