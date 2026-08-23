using InvoiceService.Application.Ports;
using InvoiceService.Domain;
using VstOnlineStore.Observability;

namespace InvoiceService.Email;

/// <summary>
/// Lokaler E-Mail-Testadapter: Statt Zugangsdaten zu einem externen SMTP-Server
/// vorauszusetzen, legt er eine vollständige MIME-Nachricht in einem Pickup-
/// Verzeichnis ab. Pickup- und SMTP-Adapter verwenden dieselbe Nachricht.
/// </summary>
public sealed class PickupDirectoryInvoiceEmailSender(
    string outboxDirectory,
    string senderAddress,
    string senderName,
    IStructuredLogger logger) : IInvoiceEmailSender {

    public async Task<string> SendAsync(
        InvoiceRecord invoice,
        CancellationToken cancellationToken = default) {

        var message = InvoiceEmailMessageFactory.Create(
            invoice,
            senderAddress,
            senderName);

        Directory.CreateDirectory(outboxDirectory);
        var fileName = $"invoice-{invoice.InvoiceId:D}.eml";
        var targetPath = Path.Combine(outboxDirectory, fileName);
        var temporaryPath = $"{targetPath}.tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous)) {
            await message.WriteToAsync(stream, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporaryPath, targetPath, overwrite: true);

        logger.Info(
            "Invoice email written to pickup directory.",
            new {
                invoice.InvoiceId,
                invoice.InvoiceNumber,
                recipientDomain = InvoiceEmailMessageFactory.GetDomain(invoice.CustomerEmail),
                outboxDirectory,
                fileName,
                attachmentSizeBytes = invoice.PdfDocument.Length
            });
        return targetPath;
    }
}
