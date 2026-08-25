namespace VstOnlineStore.Messaging;

/// <summary>
/// Prozessübergreifender Vertrag zwischen BillingService und InvoiceService.
/// Die Event-ID und Invoice-ID ermöglichen eine idempotente Verarbeitung bei
/// einer erneuten RabbitMQ-Zustellung.
/// </summary>
public sealed record PaymentSucceededEvent(
    Guid EventId,
    Guid InvoiceId,
    Guid CorrelationId,
    DateTime PaidAtUtc,
    string OrderReference,
    string CustomerEmail,
    long AmountInCents,
    string Currency,
    string PaymentProvider,
    string TransactionId,
    IReadOnlyList<PaymentSucceededLineItem> Items,
    string? PresentationScenario = null);

public sealed record PaymentSucceededLineItem(
    string ProductId,
    string Description,
    int Quantity,
    long UnitPriceInCents);
