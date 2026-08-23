using InvoiceService.Application.Ports;
using InvoiceService.Domain;
using MailKit.Security;
using VstOnlineStore.Observability;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;

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
        var message = InvoiceEmailMessageFactory.Create(
            invoice,
            options.SenderAddress,
            options.SenderName);

        using var client = new SmtpClient {
            Timeout = 10_000
        };
        var secureSocketOptions = options.Smtp.EnableSsl
            ? options.Smtp.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls
            : SecureSocketOptions.None;

        await client.ConnectAsync(
            options.Smtp.Host,
            options.Smtp.Port,
            secureSocketOptions,
            cancellationToken);
        try {
            await client.AuthenticateAsync(
                options.Smtp.UserName,
                options.Smtp.Password,
                cancellationToken);
            await client.SendAsync(message, cancellationToken);
        }
        finally {
            if (client.IsConnected) {
                await client.DisconnectAsync(true, CancellationToken.None);
            }
        }

        logger.Info(
            "Invoice email sent through SMTP adapter.",
            new {
                invoice.InvoiceId,
                invoice.InvoiceNumber,
                recipientDomain = InvoiceEmailMessageFactory.GetDomain(invoice.CustomerEmail),
                smtpHost = options.Smtp.Host,
                smtpPort = options.Smtp.Port,
                options.Smtp.EnableSsl,
                attachmentSizeBytes = invoice.PdfDocument.Length
            });
        return $"smtp://{options.Smtp.Host}:{options.Smtp.Port}";
    }
}
