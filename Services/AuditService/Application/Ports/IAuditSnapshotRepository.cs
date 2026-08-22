using AuditService.Domain;

namespace AuditService.Application.Ports;

/// <summary>
/// Persistenzport für unveränderliche Audit-Snapshots. Der PostgreSQL-Adapter
/// und der nur noch für Legacy-Importe vorhandene JSON-Adapter implementieren
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
