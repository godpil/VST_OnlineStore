using AuditContracts = VstOnlineStore.Contracts.AuditService;
using BillingContracts = VstOnlineStore.Contracts.BillingService;
using InvoiceContracts = VstOnlineStore.Contracts.InvoiceService;
using WarehouseContracts = VstOnlineStore.Contracts.WarehouseService;

namespace ShopService.Orchestration;

public sealed class ServiceStatusOrchestrator(
    WarehouseContracts.WarehouseCatalog.WarehouseCatalogClient warehouse,
    BillingContracts.BillingOperations.BillingOperationsClient billing,
    InvoiceContracts.InvoiceOperations.InvoiceOperationsClient invoice,
    AuditContracts.AuditOperations.AuditOperationsClient audit) {

    public async Task<IReadOnlyList<DownstreamServiceStatus>> GetStatusAsync(
        CancellationToken cancellationToken) {

        var warehouseTask = warehouse.GetStatusAsync(
            new WarehouseContracts.WarehouseStatusRequest(),
            cancellationToken: cancellationToken).ResponseAsync;
        var billingTask = billing.GetStatusAsync(
            new BillingContracts.BillingStatusRequest(),
            cancellationToken: cancellationToken).ResponseAsync;
        var invoiceTask = invoice.GetStatusAsync(
            new InvoiceContracts.InvoiceStatusRequest(),
            cancellationToken: cancellationToken).ResponseAsync;
        var auditTask = audit.GetStatusAsync(
            new AuditContracts.AuditStatusRequest(),
            cancellationToken: cancellationToken).ResponseAsync;

        await Task.WhenAll(warehouseTask, billingTask, invoiceTask, auditTask);

        return new DownstreamServiceStatus[] {
            new((await warehouseTask).Service, (await warehouseTask).Available),
            new((await billingTask).Service, (await billingTask).Available),
            new((await invoiceTask).Service, (await invoiceTask).Available),
            new((await auditTask).Service, (await auditTask).Available)
        };
    }
}

public sealed record DownstreamServiceStatus(string Service, bool Available);
