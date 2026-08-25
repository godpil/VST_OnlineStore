namespace InvoiceService.Messaging;

public sealed class InvoiceRetryOptions {
    public const string SectionName = "InvoiceRetry";

    public int MaxAttempts { get; set; } = 3;
    public int DelayMilliseconds { get; set; } = 750;

    public bool IsValid() =>
        MaxAttempts >= 3 &&
        MaxAttempts <= 10 &&
        DelayMilliseconds >= 0 &&
        DelayMilliseconds <= 30_000;
}
