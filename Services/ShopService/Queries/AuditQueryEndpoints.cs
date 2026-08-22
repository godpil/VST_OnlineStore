using System.Text.Json;
using Grpc.Core;
using VstOnlineStore.Observability;
using AuditContracts = VstOnlineStore.Contracts.AuditService;

namespace ShopService.Queries;

internal static class AuditQueryEndpoints {
    public static IEndpointRouteBuilder MapAuditQueryEndpoints(
        this IEndpointRouteBuilder endpoints) {

        endpoints.MapGet(
            "/api/order-audits/{correlationId}/snapshots",
            GetOrderAuditSnapshotsAsync);

        return endpoints;
    }

    private static async Task<IResult> GetOrderAuditSnapshotsAsync(
        Guid correlationId,
        AuditContracts.AuditOperations.AuditOperationsClient audit,
        IStructuredLogger logger,
        CancellationToken cancellationToken) {

        try {
            var response = await audit.GetOrderSnapshotsAsync(
                new AuditContracts.GetOrderSnapshotsRequest {
                    CorrelationId = correlationId.ToString("D")
                },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: cancellationToken);

            var snapshots = response.Snapshots.Select(snapshot => new {
                eventID = Guid.Parse(snapshot.EventId),
                correlationID = Guid.Parse(snapshot.CorrelationId),
                eventType = ToEventTypeText(snapshot.EventType),
                responsibleService = snapshot.ResponsibleService,
                timestamp = snapshot.Timestamp.ToDateTime(),
                payload = ParsePayload(snapshot.PayloadJson),
                previousEventID = string.IsNullOrWhiteSpace(snapshot.PreviousEventId)
                    ? (Guid?)null
                    : Guid.Parse(snapshot.PreviousEventId),
                actor = snapshot.Actor,
                statusCode = ToStatusCodeText(snapshot.StatusCode)
            }).ToArray();

            return Results.Ok(snapshots);
        }
        catch (RpcException exception) when (exception.StatusCode is
            StatusCode.Unavailable or StatusCode.DeadlineExceeded) {
            logger.Warn(
                "Audit snapshot query failed.",
                new {
                    correlationId,
                    grpcStatus = exception.StatusCode.ToString()
                },
                exception);

            return Results.Problem(
                title: "AuditService nicht erreichbar",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception) when (exception is JsonException or FormatException) {
            logger.Error(
                "Audit snapshot response was invalid.",
                new { correlationId },
                exception);

            return Results.Problem(
                title: "Ungültige Antwort des AuditService",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static JsonElement ParsePayload(string json) {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ToEventTypeText(
        AuditContracts.AuditEventType eventType) => eventType switch {
            AuditContracts.AuditEventType.OrderStarted => "ORDER_STARTED",
            AuditContracts.AuditEventType.OrderValidated => "ORDER_VALIDATED",
            AuditContracts.AuditEventType.StockReservation => "STOCK_RESERVATION",
            AuditContracts.AuditEventType.Payment => "PAYMENT",
            AuditContracts.AuditEventType.StockRelease => "STOCK_RELEASE",
            AuditContracts.AuditEventType.OrderCompleted => "ORDER_COMPLETED",
            AuditContracts.AuditEventType.Invoice => "INVOICE",
            _ => throw new JsonException("Der Audit-Event-Typ ist ungültig.")
        };

    private static string ToStatusCodeText(
        AuditContracts.AuditStatusCode statusCode) => statusCode switch {
            AuditContracts.AuditStatusCode.Success => "SUCCESS",
            AuditContracts.AuditStatusCode.Failure => "FAILURE",
            AuditContracts.AuditStatusCode.Compensating => "COMPENSATING",
            AuditContracts.AuditStatusCode.Compensated => "COMPENSATED",
            _ => throw new JsonException("Der Audit-Status ist ungültig.")
        };
}
