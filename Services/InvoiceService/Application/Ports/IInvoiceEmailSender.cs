using InvoiceService.Domain;

namespace InvoiceService.Application.Ports;

public interface IInvoiceEmailSender {
    Task<string> SendAsync(
        InvoiceRecord invoice,
        CancellationToken cancellationToken = default);
}
