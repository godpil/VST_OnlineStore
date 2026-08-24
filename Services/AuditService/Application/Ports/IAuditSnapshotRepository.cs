using AuditService.Domain;

namespace AuditService.Application.Ports;

/// <summary>
/// Persistenzport für unveränderliche Audit-Snapshots. Die Anwendungsschicht
/// greift ausschließlich über diese Schnittstelle auf PostgreSQL zu.
/// </summary>
public interface IAuditSnapshotRepository {
    Task<AuditSnapshot> AppendAsync(
        AuditSnapshotDraft draft,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditSnapshot>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default);
}
