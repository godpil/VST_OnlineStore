using AuditService.Domain;

namespace AuditService.Application.Ports;

/// <summary>
/// Persistenzport für unveränderliche Audit-Snapshots. Der aktuelle
/// JSON-Adapter und ein späterer Entity-Framework-Adapter implementieren
/// ausschließlich diese Schnittstelle.
/// </summary>
public interface IAuditSnapshotRepository {
    Task<AuditSnapshot> AppendAsync(
        AuditSnapshotDraft draft,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditSnapshot>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default);
}
