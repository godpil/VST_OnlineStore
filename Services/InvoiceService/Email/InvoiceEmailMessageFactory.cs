using InvoiceService.Domain;
using MimeKit;

namespace InvoiceService.Email;

internal static class InvoiceEmailMessageFactory {
    public static MimeMessage Create(
        InvoiceRecord invoice,
        string senderAddress,
        string senderName) {

        ArgumentNullException.ThrowIfNull(invoice);

        if (!MailboxAddress.TryParse(invoice.CustomerEmail, out var recipient)) {
            throw new InvalidDataException("Die Empfängeradresse der Rechnung ist ungültig.");
        }
        if (!MailboxAddress.TryParse(senderAddress, out var sender)) {
            throw new InvalidDataException("Die Absenderadresse der Rechnung ist ungültig.");
        }

        sender.Name = senderName;
        var body = new BodyBuilder {
            TextBody = "Vielen Dank für Ihre Bestellung. Ihre Rechnung befindet sich im Anhang."
        };
        body.Attachments.Add(
            $"{invoice.InvoiceNumber}.pdf",
            invoice.PdfDocument,
            ContentType.Parse("application/pdf"));

        var message = new MimeMessage {
            Subject = $"Rechnung {invoice.InvoiceNumber}",
            Date = new DateTimeOffset(invoice.CreatedAtUtc),
            MessageId = $"{invoice.InvoiceId:N}@holzwerk.example",
            Body = body.ToMessageBody()
        };
        message.From.Add(sender);
        message.To.Add(recipient);
        return message;
    }

    public static string? GetDomain(string address) {
        if (!MailboxAddress.TryParse(address, out var mailbox)) {
            return null;
        }

        var separatorIndex = mailbox.Address.LastIndexOf('@');
        return separatorIndex >= 0 && separatorIndex < mailbox.Address.Length - 1
            ? mailbox.Address[(separatorIndex + 1)..]
            : null;
    }
}
