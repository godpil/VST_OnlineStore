using System.Text.Json;
using System.Text.Json.Serialization;
using AuditService.Application.Ports;
using AuditService.Domain;
using VstOnlineStore.Observability;

namespace AuditService.Storage;

/// <summary>
/// Simuliert eine append-only Datenbanktabelle als JSON-Datei. Jeder Schreibzugriff
/// erzeugt zunächst eine vollständige temporäre Datei und ersetzt erst danach den
/// sichtbaren Datenbestand.
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

            ValidateSnapshots(snapshots);
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

    private static void ValidateSnapshots(
        IReadOnlyCollection<AuditSnapshot> snapshots) {

        if (snapshots.Any(snapshot =>
                snapshot.EventId == Guid.Empty
                || snapshot.CorrelationId == Guid.Empty
                || snapshot.Timestamp.Kind != DateTimeKind.Utc
                || snapshot.SequenceNumber <= 0
                || string.IsNullOrWhiteSpace(snapshot.ResponsibleService)
                || string.IsNullOrWhiteSpace(snapshot.Actor)
                || snapshot.Payload.ValueKind == JsonValueKind.Undefined)) {
            throw new InvalidDataException(
                "Die Audit-Datei enthält mindestens einen ungültigen Snapshot.");
        }

        if (snapshots.Select(snapshot => snapshot.EventId).Distinct().Count() != snapshots.Count
            || snapshots.Select(snapshot => snapshot.SequenceNumber).Distinct().Count() != snapshots.Count) {
            throw new InvalidDataException(
                "Event-IDs und Sequenznummern der Audit-Datei müssen eindeutig sein.");
        }

        var orderedSnapshots = snapshots
            .OrderBy(snapshot => snapshot.SequenceNumber)
            .ToArray();
        var previousByCorrelation = new Dictionary<Guid, Guid>();

        foreach (var snapshot in orderedSnapshots) {
            previousByCorrelation.TryGetValue(
                snapshot.CorrelationId,
                out var expectedPreviousEventId);
            var expected = expectedPreviousEventId == Guid.Empty
                ? (Guid?)null
                : expectedPreviousEventId;

            if (snapshot.PreviousEventId != expected) {
                throw new InvalidDataException(
                    $"Die Ereigniskette für Correlation-ID {snapshot.CorrelationId:D} ist ungültig.");
            }

            previousByCorrelation[snapshot.CorrelationId] = snapshot.EventId;
        }
    }
}
