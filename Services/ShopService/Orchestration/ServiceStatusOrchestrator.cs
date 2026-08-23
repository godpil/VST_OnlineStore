using Grpc.Core;
using VstOnlineStore.Observability;
using AuditContracts = VstOnlineStore.Contracts.AuditService;
using BillingContracts = VstOnlineStore.Contracts.BillingService;
using InvoiceContracts = VstOnlineStore.Contracts.InvoiceService;
using WarehouseContracts = VstOnlineStore.Contracts.WarehouseService;

namespace ShopService.Orchestration;

public sealed class ServiceStatusOrchestrator(
    WarehouseContracts.WarehouseCatalog.WarehouseCatalogClient warehouse,
    BillingContracts.BillingOperations.BillingOperationsClient billing,
    InvoiceContracts.InvoiceOperations.InvoiceOperationsClient invoice,
    AuditContracts.AuditOperations.AuditOperationsClient audit,
    IStructuredLogger logger) {

    public async Task<IReadOnlyList<DownstreamServiceStatus>> GetStatusAsync(
        CancellationToken cancellationToken) {

        var warehouseTask = ProbeAsync(
            "WarehouseService",
            async () => {
                var status = await warehouse.GetStatusAsync(
                    new WarehouseContracts.WarehouseStatusRequest(),
                    cancellationToken: cancellationToken);
                return new DownstreamServiceStatus(status.Service, status.Available);
            },
            cancellationToken);
        var billingTask = ProbeAsync(
            "BillingService",
            async () => {
                var status = await billing.GetStatusAsync(
                    new BillingContracts.BillingStatusRequest(),
                    cancellationToken: cancellationToken);
                return new DownstreamServiceStatus(status.Service, status.Available);
            },
            cancellationToken);
        var invoiceTask = ProbeAsync(
            "InvoiceService",
            async () => {
                var status = await invoice.GetStatusAsync(
                    new InvoiceContracts.InvoiceStatusRequest(),
                    cancellationToken: cancellationToken);
                return new DownstreamServiceStatus(status.Service, status.Available);
            },
            cancellationToken);
        var auditTask = ProbeAsync(
            "AuditService",
            async () => {
                var status = await audit.GetStatusAsync(
                    new AuditContracts.AuditStatusRequest(),
                    cancellationToken: cancellationToken);
                return new DownstreamServiceStatus(status.Service, status.Available);
            },
            cancellationToken);

        return await Task.WhenAll(
            warehouseTask,
            billingTask,
            invoiceTask,
            auditTask);
    }

    private async Task<DownstreamServiceStatus> ProbeAsync(
        string serviceName,
        Func<Task<DownstreamServiceStatus>> probe,
        CancellationToken cancellationToken) {

        try {
            return await probe();
        }
        catch (RpcException exception)
            when (!(cancellationToken.IsCancellationRequested &&
                    exception.StatusCode == StatusCode.Cancelled)) {
            logger.Error(
                "Downstream service health check failed.",
                new {
                    downstreamService = serviceName,
                    operation = "GetStatus",
                    grpcStatus = exception.StatusCode.ToString(),
                    grpcDetail = exception.Status.Detail,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);
            throw;
        }
    }
}

public sealed record DownstreamServiceStatus(string Service, bool Available);
