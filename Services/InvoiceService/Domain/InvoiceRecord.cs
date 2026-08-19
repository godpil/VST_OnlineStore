namespace InvoiceService.Domain;

public sealed record InvoiceRecord(
    Guid SourceEventId,
    Guid InvoiceId,
    Guid CorrelationId,
    string InvoiceNumber,
    string OrderReference,
    string CustomerEmail,
    DateTime CreatedAtUtc,
    DateTime PaidAtUtc,
    long AmountInCents,
    string Currency,
    string PaymentProvider,
    string TransactionId,
    IReadOnlyList<InvoiceLineItem> Items,
    byte[] PdfDocument,
    DateTime? EmailDispatchedAtUtc);

public sealed record InvoiceLineItem(
    string ProductId,
    string Description,
    int Quantity,
    long UnitPriceInCents);
