using System.Text.Json;
using AuditService.Application.Ports;
using AuditService.Domain;

namespace AuditService.Application;

public sealed class AuditApplicationService(
    IAuditSnapshotRepository repository) {

    public Task<AuditSnapshot> RecordAsync(
        Guid correlationId,
        AuditEventType eventType,
        string responsibleService,
        JsonElement payload,
        string actor,
        AuditStatusCode statusCode,
        CancellationToken cancellationToken = default) =>
        RecordAsync(
            Guid.NewGuid(),
            correlationId,
            eventType,
            responsibleService,
            DateTime.UtcNow,
            payload,
            actor,
            statusCode,
            cancellationToken);

    public Task<AuditSnapshot> RecordAsync(
        Guid eventId,
        Guid correlationId,
        AuditEventType eventType,
        string responsibleService,
        DateTime timestamp,
        JsonElement payload,
        string actor,
        AuditStatusCode statusCode,
        CancellationToken cancellationToken = default) {

        if (eventId == Guid.Empty) {
            throw new ArgumentException(
                "Die Event-ID darf nicht leer sein.",
                nameof(eventId));
        }

        if (correlationId == Guid.Empty) {
            throw new ArgumentException(
                "Die Correlation-ID darf nicht leer sein.",
                nameof(correlationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(responsibleService);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        if (timestamp.Kind != DateTimeKind.Utc) {
            throw new ArgumentException(
                "Der Audit-Zeitstempel muss in UTC vorliegen.",
                nameof(timestamp));
        }

        if (!Enum.IsDefined(eventType)) {
            throw new ArgumentOutOfRangeException(nameof(eventType));
        }

        if (!Enum.IsDefined(statusCode)) {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        if (payload.ValueKind is JsonValueKind.Undefined) {
            throw new ArgumentException(
                "Der Snapshot-Payload muss gültiges JSON enthalten.",
                nameof(payload));
        }

        return repository.AppendAsync(
            new AuditSnapshotDraft(
                eventId,
                correlationId,
                eventType,
                responsibleService.Trim(),
                timestamp,
                payload.Clone(),
                actor.Trim(),
                statusCode),
            cancellationToken);
    }

    public Task<IReadOnlyList<AuditSnapshot>> GetOrderSnapshotsAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default) {

        if (correlationId == Guid.Empty) {
            throw new ArgumentException(
                "Die Correlation-ID darf nicht leer sein.",
                nameof(correlationId));
        }

        return repository.GetByCorrelationIdAsync(
            correlationId,
            cancellationToken);
    }
}
