using System.Text.Json;
using InvoiceService.Application.Ports;
using InvoiceService.Domain;
using VstOnlineStore.Observability;

namespace InvoiceService.Storage;

/// <summary>
/// Persistiert die Rechnungen inklusive PDF als Base64-kodiertes Byte-Array in
/// der bereits im Projekt verwendeten JSON-Datenbank-Simulation.
/// </summary>
public sealed class JsonInvoiceRepository(
    string dataFilePath,
    IStructuredLogger logger) : IInvoiceRepository {

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _accessLock = new(1, 1);
    private readonly Dictionary<Guid, InvoiceRecord> _invoices = [];

    public async Task InitializeAsync(CancellationToken cancellationToken = default) {
        await _accessLock.WaitAsync(cancellationToken);
        try {
            if (!File.Exists(dataFilePath)) {
                await WriteToDiskCoreAsync([], cancellationToken);
                logger.Info("Invoice data file initialized.", new { dataFilePath });
                return;
            }

            await using var stream = File.OpenRead(dataFilePath);
            var invoices = await JsonSerializer.DeserializeAsync<List<InvoiceRecord>>(
                stream,
                JsonOptions,
                cancellationToken) ?? [];
            ValidateInvoices(invoices);

            _invoices.Clear();
            foreach (var invoice in invoices) {
                _invoices.Add(invoice.InvoiceId, invoice);
            }

            logger.Info(
                "Invoice data loaded.",
                new { invoiceCount = _invoices.Count, dataFilePath });
        }
        finally {
            _accessLock.Release();
        }
    }

    public async Task<InvoiceRecord?> GetByIdAsync(
        Guid invoiceId,
        CancellationToken cancellationToken = default) {

        await _accessLock.WaitAsync(cancellationToken);
        try {
            return _invoices.GetValueOrDefault(invoiceId);
        }
        finally {
            _accessLock.Release();
        }
    }

    public async Task<InvoiceRecord?> GetBySourceEventIdAsync(
        Guid sourceEventId,
        CancellationToken cancellationToken = default) {

        await _accessLock.WaitAsync(cancellationToken);
        try {
            return _invoices.Values.FirstOrDefault(
                invoice => invoice.SourceEventId == sourceEventId);
        }
        finally {
            _accessLock.Release();
        }
    }

    public async Task UpsertAsync(
        InvoiceRecord invoice,
        CancellationToken cancellationToken = default) {

        ValidateInvoice(invoice);
        await _accessLock.WaitAsync(cancellationToken);
        try {
            var duplicateEvent = _invoices.Values.FirstOrDefault(
                existing => existing.SourceEventId == invoice.SourceEventId
                    && existing.InvoiceId != invoice.InvoiceId);
            if (duplicateEvent is not null) {
                throw new InvalidDataException(
                    $"Die Event-ID {invoice.SourceEventId:D} ist bereits einer anderen Rechnung zugeordnet.");
            }

            var persistedInvoices = _invoices.Values
                .Where(existing => existing.InvoiceId != invoice.InvoiceId)
                .Append(invoice)
                .OrderBy(existing => existing.CreatedAtUtc)
                .ToArray();
            await WriteToDiskCoreAsync(persistedInvoices, cancellationToken);
            _invoices[invoice.InvoiceId] = invoice;

            logger.Info(
                "Invoice persisted in JSON database.",
                new {
                    invoice.InvoiceId,
                    invoice.InvoiceNumber,
                    invoice.CorrelationId,
                    invoice.SourceEventId,
                    pdfSizeBytes = invoice.PdfDocument.Length,
                    emailDispatched = invoice.EmailDispatchedAtUtc.HasValue,
                    dataFilePath
                });
        }
        finally {
            _accessLock.Release();
        }
    }

    private async Task WriteToDiskCoreAsync(
        IReadOnlyCollection<InvoiceRecord> invoices,
        CancellationToken cancellationToken) {

        var directory = Path.GetDirectoryName(dataFilePath);
        if (!string.IsNullOrWhiteSpace(directory)) {
            Directory.CreateDirectory(directory);
        }

        var temporaryFilePath = $"{dataFilePath}.tmp";
        await using (var stream = File.Create(temporaryFilePath)) {
            await JsonSerializer.SerializeAsync(
                stream,
                invoices,
                JsonOptions,
                cancellationToken);
        }

        File.Move(temporaryFilePath, dataFilePath, overwrite: true);
    }

    private static void ValidateInvoices(IReadOnlyCollection<InvoiceRecord> invoices) {
        foreach (var invoice in invoices) {
            ValidateInvoice(invoice);
        }

        if (invoices.Select(invoice => invoice.InvoiceId).Distinct().Count() != invoices.Count
            || invoices.Select(invoice => invoice.SourceEventId).Distinct().Count() != invoices.Count) {
            throw new InvalidDataException(
                "Rechnungs-IDs und Quell-Event-IDs müssen eindeutig sein.");
        }
    }

    private static void ValidateInvoice(InvoiceRecord invoice) {
        if (invoice.SourceEventId == Guid.Empty
            || invoice.InvoiceId == Guid.Empty
            || invoice.CorrelationId == Guid.Empty
            || invoice.CreatedAtUtc.Kind != DateTimeKind.Utc
            || invoice.PaidAtUtc.Kind != DateTimeKind.Utc
            || invoice.AmountInCents <= 0
            || invoice.PdfDocument.Length == 0
            || invoice.Items.Count == 0
            || string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            || string.IsNullOrWhiteSpace(invoice.CustomerEmail)
            || string.IsNullOrWhiteSpace(invoice.Currency)) {
            throw new InvalidDataException("Die Rechnung enthält ungültige Pflichtfelder.");
        }
    }
}
