using AuditService.Domain;

namespace AuditService.Storage;

internal static class AuditSnapshotValidator {
    public static void Validate(IReadOnlyCollection<AuditSnapshot> snapshots) {
        if (snapshots.Any(snapshot =>
                snapshot.EventId == Guid.Empty
                || snapshot.CorrelationId == Guid.Empty
                || snapshot.Timestamp.Kind != DateTimeKind.Utc
                || snapshot.SequenceNumber <= 0
                || string.IsNullOrWhiteSpace(snapshot.ResponsibleService)
                || string.IsNullOrWhiteSpace(snapshot.Actor)
                || snapshot.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined)) {
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
