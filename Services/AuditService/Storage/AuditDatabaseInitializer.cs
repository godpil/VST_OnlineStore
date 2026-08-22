using System.Text.Json;
using System.Text.Json.Serialization;
using AuditService.Domain;
using Microsoft.EntityFrameworkCore;
using VstOnlineStore.Observability;

namespace AuditService.Storage;

public sealed class AuditDatabaseInitializer(
    IDbContextFactory<AuditDbContext> contextFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    IStructuredLogger logger) {

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default) {

        await using var context = await contextFactory.CreateDbContextAsync(
            cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
        await ImportLegacyJsonAsync(context, cancellationToken);
    }

    private async Task ImportLegacyJsonAsync(
        AuditDbContext context,
        CancellationToken cancellationToken) {

        var configuredPath = configuration["AuditData:LegacyJsonFilePath"]
            ?? "Data/audit-snapshots.json";
        var filePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(
                environment.ContentRootPath,
                configuredPath));
        if (!File.Exists(filePath)) {
            return;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('vst-audit-json-import', 0));",
            cancellationToken);

        if (await context.Snapshots.AnyAsync(cancellationToken)) {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await using var stream = File.OpenRead(filePath);
        var snapshots = await JsonSerializer.DeserializeAsync<List<AuditSnapshot>>(
            stream,
            JsonOptions,
            cancellationToken) ?? [];
        if (snapshots.Count == 0) {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        AuditSnapshotValidator.Validate(snapshots);
        context.Snapshots.AddRange(
            snapshots
                .OrderBy(snapshot => snapshot.SequenceNumber)
                .Select(AuditSnapshotEntity.FromDomain));
        await context.SaveChangesAsync(cancellationToken);

        var highestSequenceNumber = snapshots.Max(snapshot => snapshot.SequenceNumber);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT setval(pg_get_serial_sequence('audit_snapshots', 'sequence_number'), {highestSequenceNumber}, true);",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.Info(
            "Legacy audit snapshots imported into PostgreSQL.",
            new {
                snapshotCount = snapshots.Count,
                filePath,
                highestSequenceNumber
            });
    }
}
