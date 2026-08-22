using System.Text.Json;
using System.Text.Json.Serialization;
using AuditService.Application.Ports;
using AuditService.Domain;
using VstOnlineStore.Observability;

namespace AuditService.Storage;

/// <summary>
/// Legacy-Adapter für die frühere JSON-Persistenz und lokale Datenübernahmen.
/// Die aktive Laufzeitpersistenz erfolgt über PostgreSQL.
/// </summary>
public sealed class JsonAuditSnapshotRepository(
    string dataFilePath,
    IStructuredLogger logger) : IAuditSnapshotRepository {

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _accessLock = new(1, 1);
    private readonly List<AuditSnapshot> _snapshots = [];

    public async Task ReadFromDiskAsync(
        CancellationToken cancellationToken = default) {

        await _accessLock.WaitAsync(cancellationToken);
        try {
            if (!File.Exists(dataFilePath)) {
                await WriteToDiskCoreAsync([], cancellationToken);
                logger.Info(
                    "Audit data file initialized.",
                    new { dataFilePath });
                return;
            }

            await using var stream = File.OpenRead(dataFilePath);
            var snapshots = await JsonSerializer.DeserializeAsync<List<AuditSnapshot>>(
                stream,
                JsonOptions,
                cancellationToken) ?? [];

            AuditSnapshotValidator.Validate(snapshots);
            _snapshots.Clear();
            _snapshots.AddRange(snapshots.OrderBy(snapshot => snapshot.SequenceNumber));

            logger.Info(
                "Audit data loaded.",
                new {
                    snapshotCount = _snapshots.Count,
                    dataFilePath
                });
        }
        finally {
            _accessLock.Release();
        }
    }

    public async Task<AuditSnapshot> AppendAsync(
        AuditSnapshotDraft draft,
        CancellationToken cancellationToken = default) {

        await _accessLock.WaitAsync(cancellationToken);
        try {
            var existingSnapshot = _snapshots.FirstOrDefault(
                snapshot => snapshot.EventId == draft.EventId);
            if (existingSnapshot is not null) {
                if (existingSnapshot.CorrelationId != draft.CorrelationId) {
                    throw new InvalidDataException(
                        $"Die Event-ID {draft.EventId:D} wurde mit unterschiedlichen Correlation-IDs empfangen.");
                }

                return existingSnapshot;
            }

            var previousEventId = _snapshots
                .Where(snapshot => snapshot.CorrelationId == draft.CorrelationId)
                .OrderByDescending(snapshot => snapshot.SequenceNumber)
                .Select(snapshot => (Guid?)snapshot.EventId)
                .FirstOrDefault();
            var nextSequenceNumber = _snapshots.Count == 0
                ? 1
                : checked(_snapshots.Max(snapshot => snapshot.SequenceNumber) + 1);

            var snapshot = new AuditSnapshot(
                draft.EventId,
                draft.CorrelationId,
                draft.EventType,
                draft.ResponsibleService,
                draft.Timestamp,
                draft.Payload.Clone(),
                previousEventId,
                draft.Actor,
                draft.StatusCode,
                nextSequenceNumber);
            var persistedSnapshots = _snapshots.Append(snapshot).ToArray();

            await WriteToDiskCoreAsync(persistedSnapshots, cancellationToken);
            _snapshots.Add(snapshot);

            return snapshot;
        }
        finally {
            _accessLock.Release();
        }
    }

    public async Task<IReadOnlyList<AuditSnapshot>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default) {

        await _accessLock.WaitAsync(cancellationToken);
        try {
            return _snapshots
                .Where(snapshot => snapshot.CorrelationId == correlationId)
                .OrderBy(snapshot => snapshot.Timestamp)
                .ThenBy(snapshot => snapshot.SequenceNumber)
                .ToArray();
        }
        finally {
            _accessLock.Release();
        }
    }

    private async Task WriteToDiskCoreAsync(
        IReadOnlyCollection<AuditSnapshot> snapshots,
        CancellationToken cancellationToken) {

        var directory = Path.GetDirectoryName(dataFilePath);
        if (!string.IsNullOrWhiteSpace(directory)) {
            Directory.CreateDirectory(directory);
        }

        var temporaryFilePath = $"{dataFilePath}.tmp";
        await using (var stream = File.Create(temporaryFilePath)) {
            await JsonSerializer.SerializeAsync(
                stream,
                snapshots.OrderBy(snapshot => snapshot.SequenceNumber),
                JsonOptions,
                cancellationToken);
        }

        File.Move(temporaryFilePath, dataFilePath, overwrite: true);
    }

}
