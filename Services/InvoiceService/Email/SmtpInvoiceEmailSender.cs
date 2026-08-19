using System.Net;
using System.Net.Mail;
using InvoiceService.Application.Ports;
using InvoiceService.Domain;
using VstOnlineStore.Observability;

namespace InvoiceService.Email;

public sealed class SmtpInvoiceEmailSender(
    InvoiceEmailOptions options,
    IStructuredLogger logger) : IInvoiceEmailSender {

    public async Task<string> SendAsync(
        InvoiceRecord invoice,
        CancellationToken cancellationToken = default) {

        options.Validate();
        if (!options.UsesSmtp) {
            throw new InvalidOperationException("Der SMTP-E-Mail-Adapter ist nicht aktiviert.");
        }
        if (!MailAddress.TryCreate(invoice.CustomerEmail, out var recipient)
            || !MailAddress.TryCreate(options.SenderAddress, out var sender)) {
            throw new InvalidDataException("Absender- oder Empfängeradresse ist ungültig.");
        }

        using var message = new MailMessage {
            From = new MailAddress(sender.Address, options.SenderName),
            Subject = $"Rechnung {invoice.InvoiceNumber}",
            Body = "Vielen Dank für Ihre Bestellung. Ihre Rechnung befindet sich im Anhang.",
            IsBodyHtml = false
        };
        message.To.Add(recipient);
        var pdfStream = new MemoryStream(invoice.PdfDocument, writable: false);
        message.Attachments.Add(new Attachment(
            pdfStream,
            $"{invoice.InvoiceNumber}.pdf",
            "application/pdf"));

        using var client = new SmtpClient(options.Smtp.Host, options.Smtp.Port) {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = options.Smtp.EnableSsl,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(
                options.Smtp.UserName,
                options.Smtp.Password),
            Timeout = 10_000
        };
        await client.SendMailAsync(message, cancellationToken);

        logger.Info(
            "Invoice email sent through SMTP adapter.",
            new {
                invoice.InvoiceId,
                invoice.InvoiceNumber,
                recipientDomain = recipient.Host,
                smtpHost = options.Smtp.Host,
                smtpPort = options.Smtp.Port,
                options.Smtp.EnableSsl,
                attachmentSizeBytes = invoice.PdfDocument.Length
            });
        return $"smtp://{options.Smtp.Host}:{options.Smtp.Port}";
    }
}
