using System.Net.Mail;
using System.Text;
using InvoiceService.Application.Ports;
using InvoiceService.Domain;
using VstOnlineStore.Observability;

namespace InvoiceService.Email;

/// <summary>
/// Lokaler E-Mail-Testadapter: Statt Zugangsdaten zu einem externen SMTP-Server
/// vorauszusetzen, legt er eine vollständige MIME-Nachricht in einem Pickup-
/// Verzeichnis ab. Ein späterer SMTP-Adapter kann denselben Port implementieren.
/// </summary>
public sealed class PickupDirectoryInvoiceEmailSender(
    string outboxDirectory,
    string senderAddress,
    string senderName,
    IStructuredLogger logger) : IInvoiceEmailSender {

    public async Task<string> SendAsync(
        InvoiceRecord invoice,
        CancellationToken cancellationToken = default) {

        if (!MailAddress.TryCreate(invoice.CustomerEmail, out var recipient)) {
            throw new InvalidDataException("Die Empfängeradresse der Rechnung ist ungültig.");
        }
        if (!MailAddress.TryCreate(senderAddress, out var sender)) {
            throw new InvalidDataException("Die Absenderadresse der Rechnung ist ungültig.");
        }

        Directory.CreateDirectory(outboxDirectory);
        var fileName = $"invoice-{invoice.InvoiceId:D}.eml";
        var targetPath = Path.Combine(outboxDirectory, fileName);
        var temporaryPath = $"{targetPath}.tmp";
        var boundary = $"vst-invoice-{invoice.InvoiceId:N}";
        var pdfFileName = $"{invoice.InvoiceNumber}.pdf";
        var body = BuildMessage(
            invoice,
            sender,
            senderName,
            recipient,
            boundary,
            pdfFileName);

        await File.WriteAllTextAsync(
            temporaryPath,
            body,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        File.Move(temporaryPath, targetPath, overwrite: true);

        logger.Info(
            "Invoice email written to pickup directory.",
            new {
                invoice.InvoiceId,
                invoice.InvoiceNumber,
                recipientDomain = recipient.Host,
                outboxDirectory,
                fileName,
                attachmentSizeBytes = invoice.PdfDocument.Length
            });
        return targetPath;
    }

    private static string BuildMessage(
        InvoiceRecord invoice,
        MailAddress sender,
        string senderName,
        MailAddress recipient,
        string boundary,
        string pdfFileName) {

        var base64 = Convert.ToBase64String(invoice.PdfDocument);
        var wrappedBase64 = string.Join(
            "\r\n",
            Enumerable.Range(0, (base64.Length + 75) / 76)
                .Select(index => base64.Substring(
                    index * 76,
                    Math.Min(76, base64.Length - index * 76))));

        var builder = new StringBuilder();
        builder.Append("From: ").Append(senderName).Append(" <")
            .Append(sender.Address).Append(">\r\n")
            .Append("To: ").Append(recipient.Address).Append("\r\n")
            .Append("Subject: Rechnung ").Append(invoice.InvoiceNumber).Append("\r\n")
            .Append("Date: ").Append(invoice.CreatedAtUtc.ToString("R")).Append("\r\n")
            .Append("Message-ID: <").Append(invoice.InvoiceId.ToString("N"))
            .Append("@holzwerk.example>\r\n")
            .Append("MIME-Version: 1.0\r\n")
            .Append("Content-Type: multipart/mixed; boundary=\"").Append(boundary).Append("\"\r\n\r\n")
            .Append("--").Append(boundary).Append("\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append("Content-Transfer-Encoding: 8bit\r\n\r\n")
            .Append("Vielen Dank für Ihre Bestellung. Ihre Rechnung befindet sich im Anhang.\r\n\r\n")
            .Append("--").Append(boundary).Append("\r\n")
            .Append("Content-Type: application/pdf; name=\"").Append(pdfFileName).Append("\"\r\n")
            .Append("Content-Disposition: attachment; filename=\"").Append(pdfFileName).Append("\"\r\n")
            .Append("Content-Transfer-Encoding: base64\r\n\r\n")
            .Append(wrappedBase64).Append("\r\n")
            .Append("--").Append(boundary).Append("--\r\n");
        return builder.ToString();
    }
}
