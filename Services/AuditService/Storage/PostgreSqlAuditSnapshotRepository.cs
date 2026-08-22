using AuditService.Application.Ports;
using AuditService.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuditService.Storage;

public sealed class PostgreSqlAuditSnapshotRepository(
    IDbContextFactory<AuditDbContext> contextFactory) : IAuditSnapshotRepository {

    public async Task<AuditSnapshot> AppendAsync(
        AuditSnapshotDraft draft,
        CancellationToken cancellationToken = default) {

        try {
            await using var context = await contextFactory.CreateDbContextAsync(
                cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(
                cancellationToken);

            // Serialisiert nur Ereignisse derselben Bestellung. Mehrere
            // AuditService-Instanzen können dadurch parallel arbeiten, ohne
            // dieselbe previousEventID zu vergeben.
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({draft.CorrelationId.ToString("D")}, 0));",
                cancellationToken);

            var existing = await context.Snapshots
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    snapshot => snapshot.EventId == draft.EventId,
                    cancellationToken);
            if (existing is not null) {
                await transaction.CommitAsync(cancellationToken);
                return ValidateDuplicate(existing, draft).ToDomain();
            }

            var previousEventId = await context.Snapshots
                .AsNoTracking()
                .Where(snapshot => snapshot.CorrelationId == draft.CorrelationId)
                .OrderByDescending(snapshot => snapshot.SequenceNumber)
                .Select(snapshot => (Guid?)snapshot.EventId)
                .FirstOrDefaultAsync(cancellationToken);

            var entity = new AuditSnapshotEntity {
                EventId = draft.EventId,
                CorrelationId = draft.CorrelationId,
                EventType = draft.EventType,
                ResponsibleService = draft.ResponsibleService,
                Timestamp = draft.Timestamp,
                Payload = draft.Payload.Clone(),
                PreviousEventId = previousEventId,
                Actor = draft.Actor,
                StatusCode = draft.StatusCode
            };

            context.Snapshots.Add(entity);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return entity.ToDomain();
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException {
                SqlState: PostgresErrorCodes.UniqueViolation
            }) {

            // Eine andere Instanz kann dieselbe Event-ID zwischen Prüfung und
            // INSERT persistiert haben. Die idempotente Semantik bleibt erhalten.
            await using var lookupContext = await contextFactory.CreateDbContextAsync(
                cancellationToken);
            var existing = await lookupContext.Snapshots
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    snapshot => snapshot.EventId == draft.EventId,
                    cancellationToken);
            if (existing is null) {
                throw;
            }

            return ValidateDuplicate(existing, draft).ToDomain();
        }
    }

    public async Task<IReadOnlyList<AuditSnapshot>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default) {

        await using var context = await contextFactory.CreateDbContextAsync(
            cancellationToken);
        var snapshots = await context.Snapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.CorrelationId == correlationId)
            .OrderBy(snapshot => snapshot.Timestamp)
            .ThenBy(snapshot => snapshot.SequenceNumber)
            .ToArrayAsync(cancellationToken);
        return snapshots.Select(snapshot => snapshot.ToDomain()).ToArray();
    }

    private static AuditSnapshotEntity ValidateDuplicate(
        AuditSnapshotEntity existing,
        AuditSnapshotDraft draft) {

        if (existing.CorrelationId != draft.CorrelationId) {
            throw new InvalidDataException(
                $"Die Event-ID {draft.EventId:D} wurde mit unterschiedlichen Correlation-IDs empfangen.");
        }

        return existing;
    }
}
