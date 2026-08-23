using System.Diagnostics;
using Grpc.Core;
using Microsoft.Extensions.Options;
using VstOnlineStore.Observability;
using AuditContracts = VstOnlineStore.Contracts.AuditService;
using BillingContracts = VstOnlineStore.Contracts.BillingService;
using InvoiceContracts = VstOnlineStore.Contracts.InvoiceService;
using WarehouseContracts = VstOnlineStore.Contracts.WarehouseService;

namespace ShopService.Orchestration;

public interface IServiceReadinessService {
    Task<IReadOnlyList<DownstreamServiceStatus>> GetStatusAsync(
        CancellationToken cancellationToken);
}

public sealed class ServiceStatusOrchestrator(
    WarehouseContracts.WarehouseCatalog.WarehouseCatalogClient warehouse,
    BillingContracts.BillingOperations.BillingOperationsClient billing,
    InvoiceContracts.InvoiceOperations.InvoiceOperationsClient invoice,
    AuditContracts.AuditOperations.AuditOperationsClient audit,
    IOptions<ShopServiceTimeoutOptions> configuredTimeouts,
    IStructuredLogger logger) : IServiceReadinessService {

    private readonly ShopServiceTimeoutOptions _timeouts = configuredTimeouts.Value;

    public async Task<IReadOnlyList<DownstreamServiceStatus>> GetStatusAsync(
        CancellationToken cancellationToken) {

        var deadline = DateTime.UtcNow.Add(_timeouts.StatusProbe);
        var warehouseTask = ProbeAsync(
            "WarehouseService",
            async () => {
                var status = await warehouse.GetStatusAsync(
                    new WarehouseContracts.WarehouseStatusRequest(),
                    deadline: deadline,
                    cancellationToken: cancellationToken);
                return (status.Service, status.Available);
            },
            cancellationToken);
        var billingTask = ProbeAsync(
            "BillingService",
            async () => {
                var status = await billing.GetStatusAsync(
                    new BillingContracts.BillingStatusRequest(),
                    deadline: deadline,
                    cancellationToken: cancellationToken);
                return (status.Service, status.Available);
            },
            cancellationToken);
        var invoiceTask = ProbeAsync(
            "InvoiceService",
            async () => {
                var status = await invoice.GetStatusAsync(
                    new InvoiceContracts.InvoiceStatusRequest(),
                    deadline: deadline,
                    cancellationToken: cancellationToken);
                return (status.Service, status.Available);
            },
            cancellationToken);
        var auditTask = ProbeAsync(
            "AuditService",
            async () => {
                var status = await audit.GetStatusAsync(
                    new AuditContracts.AuditStatusRequest(),
                    deadline: deadline,
                    cancellationToken: cancellationToken);
                return (status.Service, status.Available);
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
        Func<Task<(string Service, bool Available)>> probe,
        CancellationToken cancellationToken) {

        var stopwatch = Stopwatch.StartNew();
        try {
            var status = await probe();
            stopwatch.Stop();
            if (status.Available) {
                return new DownstreamServiceStatus(
                    status.Service,
                    true,
                    "AVAILABLE",
                    "Der Service ist betriebsbereit.",
                    stopwatch.ElapsedMilliseconds);
            }

            logger.Error(
                "Downstream service reported itself unavailable.",
                new {
                    downstreamService = serviceName,
                    operation = "GetStatus",
                    durationMs = stopwatch.ElapsedMilliseconds
                });
            return new DownstreamServiceStatus(
                serviceName,
                false,
                "UNAVAILABLE",
                "Der Service meldet sich als nicht betriebsbereit.",
                stopwatch.ElapsedMilliseconds);
        }
        catch (RpcException exception)
            when (!(cancellationToken.IsCancellationRequested &&
                    exception.StatusCode == StatusCode.Cancelled)) {
            stopwatch.Stop();
            var failureKind = exception.StatusCode == StatusCode.DeadlineExceeded
                ? "TIMEOUT"
                : exception.StatusCode == StatusCode.Unavailable
                    ? "UNAVAILABLE"
                    : "ERROR";
            var message = failureKind switch {
                "TIMEOUT" => "Der Service hat nicht rechtzeitig geantwortet.",
                "UNAVAILABLE" => "Der Service ist nicht erreichbar.",
                _ => "Die Betriebszustandspruefung ist fehlgeschlagen."
            };

            logger.Error(
                "Downstream service health check failed.",
                new {
                    downstreamService = serviceName,
                    operation = "GetStatus",
                    failureKind,
                    grpcStatus = exception.StatusCode.ToString(),
                    grpcDetail = exception.Status.Detail,
                    durationMs = stopwatch.ElapsedMilliseconds,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);
            return new DownstreamServiceStatus(
                serviceName,
                false,
                failureKind,
                message,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
            when (!(cancellationToken.IsCancellationRequested &&
                    exception is OperationCanceledException)) {
            stopwatch.Stop();
            logger.Error(
                "Downstream service health check failed unexpectedly.",
                new {
                    downstreamService = serviceName,
                    operation = "GetStatus",
                    failureKind = "ERROR",
                    durationMs = stopwatch.ElapsedMilliseconds,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);
            return new DownstreamServiceStatus(
                serviceName,
                false,
                "ERROR",
                "Die Betriebszustandspruefung ist fehlgeschlagen.",
                stopwatch.ElapsedMilliseconds);
        }
    }
}

public static class ServiceReadiness {
    public static bool IsOperational(
        IReadOnlyList<DownstreamServiceStatus> statuses) =>
        statuses.Count > 0 && statuses.All(status => status.Available);

    public static int GetHttpStatusCode(
        IReadOnlyList<DownstreamServiceStatus> statuses) {

        if (statuses.Any(status => status.FailureKind == "TIMEOUT")) {
            return StatusCodes.Status504GatewayTimeout;
        }

        if (statuses.Any(status => status.FailureKind == "ERROR")) {
            return StatusCodes.Status502BadGateway;
        }

        return StatusCodes.Status503ServiceUnavailable;
    }
}

public sealed record DownstreamServiceStatus(
    string Service,
    bool Available,
    string FailureKind,
    string Message,
    long DurationMs);
