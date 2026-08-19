namespace VstOnlineStore.Messaging;

public sealed class RabbitMqInvoiceOptions {
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string BillingExchange { get; set; } = "vst.billing.events";
    public string InvoiceQueue { get; set; } = "vst.invoice.payment-succeeded";
    public string InvoiceDeadLetterExchange { get; set; } = "vst.invoice.dead-letter";
    public string InvoiceDeadLetterQueue { get; set; } = "vst.invoice.payment-succeeded.dead-letter";

    public void Validate() {
        ArgumentException.ThrowIfNullOrWhiteSpace(HostName);
        ArgumentOutOfRangeException.ThrowIfLessThan(Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Port, 65535);
        ArgumentException.ThrowIfNullOrWhiteSpace(VirtualHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(UserName);
        ArgumentException.ThrowIfNullOrWhiteSpace(BillingExchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(InvoiceQueue);
        ArgumentException.ThrowIfNullOrWhiteSpace(InvoiceDeadLetterExchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(InvoiceDeadLetterQueue);
    }
}
