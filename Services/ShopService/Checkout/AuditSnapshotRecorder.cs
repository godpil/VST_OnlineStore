using System.Text.Json;
using Grpc.Core;
using VstOnlineStore.Observability;
using AuditContracts = VstOnlineStore.Contracts.AuditService;

namespace ShopService.Checkout;

/// <summary>
/// Schreibt fachliche Order-Snapshots best effort. Eine nicht verfügbare
/// Audit-Senke darf Zahlung oder Kompensation nicht blockieren.
/// </summary>
public sealed class AuditSnapshotRecorder(
    AuditContracts.AuditOperations.AuditOperationsClient audit,
    IHttpContextAccessor httpContextAccessor,
    IStructuredLogger logger) {

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task RecordAsync(
        AuditContracts.AuditEventType eventType,
        string responsibleService,
        object payload,
        string actor,
        AuditContracts.AuditStatusCode statusCode,
        CancellationToken cancellationToken) {

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null
            || !CorrelationId.TryGet(httpContext, out var correlationId)) {
            TryLogFailure(
                null,
                eventType,
                "Für den Audit-Snapshot ist keine Correlation-ID verfügbar.");
            return;
        }

        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            await audit.RecordSnapshotAsync(
                new AuditContracts.RecordAuditSnapshotRequest {
                    CorrelationId = correlationId.ToString("D"),
                    EventType = eventType,
                    ResponsibleService = responsibleService,
                    PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
                    Actor = actor,
                    StatusCode = statusCode
                },
                deadline: DateTime.UtcNow.AddSeconds(2),
                cancellationToken: timeout.Token);
        }
        catch (Exception exception) when (exception is RpcException
            or OperationCanceledException
            or JsonException) {
            TryLogFailure(
                exception,
                eventType,
                "Audit-Snapshot konnte nicht geschrieben werden.");
        }
    }

    private void TryLogFailure(
        Exception? exception,
        AuditContracts.AuditEventType eventType,
        string message) {

        try {
            logger.Warn(
                message,
                new {
                    eventType = eventType.ToString(),
                    exceptionType = exception?.GetType().FullName,
                    exceptionMessage = exception?.Message
                },
                exception);
        }
        catch (Exception) {
            // Die Audit-Fehlerbehandlung selbst darf den Checkout nicht stören.
        }
    }
}
