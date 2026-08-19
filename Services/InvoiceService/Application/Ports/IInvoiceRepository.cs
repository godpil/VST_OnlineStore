using InvoiceService.Domain;

namespace InvoiceService.Application.Ports;

public interface IInvoiceRepository {
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<InvoiceRecord?> GetByIdAsync(
        Guid invoiceId,
        CancellationToken cancellationToken = default);

    Task<InvoiceRecord?> GetBySourceEventIdAsync(
        Guid sourceEventId,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        InvoiceRecord invoice,
        CancellationToken cancellationToken = default);
}
