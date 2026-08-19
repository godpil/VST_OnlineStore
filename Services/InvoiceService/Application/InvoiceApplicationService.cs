using System.Net.Mail;
using InvoiceService.Application.Ports;
using InvoiceService.Domain;
using VstOnlineStore.Messaging;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;

namespace InvoiceService.Application;

public sealed class InvoiceApplicationService(
    IInvoiceRepository repository,
    IInvoicePdfRenderer pdfRenderer,
    IInvoiceEmailSender emailSender,
    IAuditEventPublisher auditPublisher,
    IStructuredLogger logger) {

    public async Task<InvoiceRecord> ProcessPaymentSucceededAsync(
        PaymentSucceededEvent paymentEvent,
        CancellationToken cancellationToken = default) {

        Validate(paymentEvent);
        using var correlationScope = CorrelationId.BeginScope(paymentEvent.CorrelationId);

        logger.Info(
            "PaymentSucceeded event received for invoice processing.",
            new {
                paymentEvent.EventId,
                paymentEvent.InvoiceId,
                paymentEvent.OrderReference,
                paymentEvent.PaymentProvider,
                recipientDomain = GetRecipientDomain(paymentEvent.CustomerEmail),
                itemCount = paymentEvent.Items.Count
            });

        var existingByEvent = await repository.GetBySourceEventIdAsync(
            paymentEvent.EventId,
            cancellationToken);
        if (existingByEvent is not null
            && existingByEvent.InvoiceId != paymentEvent.InvoiceId) {
            throw new InvalidDataException(
                "Das Zahlungsereignis ist bereits einer anderen Rechnung zugeordnet.");
        }

        var invoice = existingByEvent ?? await repository.GetByIdAsync(
            paymentEvent.InvoiceId,
            cancellationToken);

        if (invoice is null) {
            var invoiceNumber = CreateInvoiceNumber(paymentEvent);
            await PublishAuditAsync(
                paymentEvent,
                "INVOICE_GENERATION_STARTED",
                AuditStatusCode.SUCCESS,
                cancellationToken);

            var pdf = pdfRenderer.Render(paymentEvent, invoiceNumber);
            invoice = new InvoiceRecord(
                paymentEvent.EventId,
                paymentEvent.InvoiceId,
                paymentEvent.CorrelationId,
                invoiceNumber,
                paymentEvent.OrderReference,
                paymentEvent.CustomerEmail,
                DateTime.UtcNow,
                paymentEvent.PaidAtUtc,
                paymentEvent.AmountInCents,
                paymentEvent.Currency.ToUpperInvariant(),
                paymentEvent.PaymentProvider,
                paymentEvent.TransactionId,
                paymentEvent.Items.Select(item => new InvoiceLineItem(
                    item.ProductId,
                    item.Description,
                    item.Quantity,
                    item.UnitPriceInCents)).ToArray(),
                pdf,
                EmailDispatchedAtUtc: null);
            await repository.UpsertAsync(invoice, cancellationToken);

            logger.Info(
                "Invoice PDF generated and persisted.",
                new {
                    invoice.InvoiceId,
                    invoice.InvoiceNumber,
                    invoice.OrderReference,
                    pdfSizeBytes = invoice.PdfDocument.Length
                });
            await PublishAuditAsync(
                paymentEvent,
                "INVOICE_GENERATED",
                AuditStatusCode.SUCCESS,
                cancellationToken,
                invoice.InvoiceNumber,
                invoice.PdfDocument.Length);
        }
        else {
            ValidateIdentity(invoice, paymentEvent);
            logger.Info(
                "Invoice event was delivered again; existing PDF is reused.",
                new {
                    paymentEvent.EventId,
                    invoice.InvoiceId,
                    invoice.InvoiceNumber,
                    emailAlreadyDispatched = invoice.EmailDispatchedAtUtc.HasValue
                });
        }

        if (!invoice.EmailDispatchedAtUtc.HasValue) {
            var outboxPath = await emailSender.SendAsync(invoice, cancellationToken);
            invoice = invoice with { EmailDispatchedAtUtc = DateTime.UtcNow };
            await repository.UpsertAsync(invoice, cancellationToken);

            logger.Info(
                "Invoice email dispatch completed by pickup adapter.",
                new {
                    invoice.InvoiceId,
                    invoice.InvoiceNumber,
                    recipientDomain = GetRecipientDomain(invoice.CustomerEmail),
                    outboxFileName = Path.GetFileName(outboxPath)
                });
            await PublishAuditAsync(
                paymentEvent,
                "INVOICE_EMAIL_DISPATCHED",
                AuditStatusCode.SUCCESS,
                cancellationToken,
                invoice.InvoiceNumber,
                invoice.PdfDocument.Length);
        }

        return invoice;
    }

    public Task<InvoiceRecord?> GetByIdAsync(
        Guid invoiceId,
        CancellationToken cancellationToken = default) =>
        repository.GetByIdAsync(invoiceId, cancellationToken);

    private Task PublishAuditAsync(
        PaymentSucceededEvent paymentEvent,
        string phase,
        AuditStatusCode statusCode,
        CancellationToken cancellationToken,
        string? invoiceNumber = null,
        int? pdfSizeBytes = null) =>
        auditPublisher.PublishAsync(
            AuditEventType.INVOICE,
            "InvoiceService",
            new {
                phase,
                paymentEvent.EventId,
                paymentEvent.InvoiceId,
                invoiceNumber,
                paymentEvent.OrderReference,
                paymentEvent.PaymentProvider,
                paymentEvent.TransactionId,
                recipientDomain = GetRecipientDomain(paymentEvent.CustomerEmail),
                paymentEvent.AmountInCents,
                paymentEvent.Currency,
                pdfSizeBytes
            },
            "InvoiceService",
            statusCode,
            paymentEvent.CorrelationId,
            cancellationToken);

    private static string CreateInvoiceNumber(PaymentSucceededEvent paymentEvent) =>
        $"RE-{paymentEvent.PaidAtUtc:yyyyMMdd}-{paymentEvent.InvoiceId:N}"[..20].ToUpperInvariant();

    private static void Validate(PaymentSucceededEvent paymentEvent) {
        ArgumentNullException.ThrowIfNull(paymentEvent);
        var calculatedAmount = paymentEvent.Items.Sum(item =>
            checked(item.UnitPriceInCents * item.Quantity));
        if (paymentEvent.EventId == Guid.Empty
            || paymentEvent.InvoiceId == Guid.Empty
            || paymentEvent.CorrelationId == Guid.Empty
            || paymentEvent.PaidAtUtc.Kind != DateTimeKind.Utc
            || paymentEvent.AmountInCents <= 0
            || paymentEvent.Items.Count == 0
            || paymentEvent.Items.Any(item => item.Quantity <= 0
                || item.UnitPriceInCents < 0
                || string.IsNullOrWhiteSpace(item.Description))
            || calculatedAmount != paymentEvent.AmountInCents
            || !MailAddress.TryCreate(paymentEvent.CustomerEmail, out _)) {
            throw new InvalidDataException("Das PaymentSucceeded-Ereignis ist ungültig.");
        }
    }

    private static void ValidateIdentity(
        InvoiceRecord invoice,
        PaymentSucceededEvent paymentEvent) {

        if (invoice.SourceEventId != paymentEvent.EventId
            || invoice.CorrelationId != paymentEvent.CorrelationId
            || invoice.AmountInCents != paymentEvent.AmountInCents
            || !string.Equals(invoice.CustomerEmail, paymentEvent.CustomerEmail,
                StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException(
                "Eine vorhandene Rechnung widerspricht dem erneut zugestellten Ereignis.");
        }
    }

    private static string? GetRecipientDomain(string email) =>
        MailAddress.TryCreate(email, out var address) ? address.Host : null;
}
