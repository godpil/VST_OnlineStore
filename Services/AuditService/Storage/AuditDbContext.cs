using Microsoft.EntityFrameworkCore;

namespace AuditService.Storage;

public sealed class AuditDbContext(
    DbContextOptions<AuditDbContext> options) : DbContext(options) {

    internal DbSet<AuditSnapshotEntity> Snapshots => Set<AuditSnapshotEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        var snapshot = modelBuilder.Entity<AuditSnapshotEntity>();

        snapshot.ToTable("audit_snapshots", table => {
            table.HasCheckConstraint(
                "ck_audit_snapshots_sequence_number_positive",
                "sequence_number > 0");
            table.HasCheckConstraint(
                "ck_audit_snapshots_responsible_service_not_blank",
                "length(btrim(responsible_service)) > 0");
            table.HasCheckConstraint(
                "ck_audit_snapshots_actor_not_blank",
                "length(btrim(actor)) > 0");
        });

        snapshot.HasKey(entity => entity.EventId)
            .HasName("pk_audit_snapshots");
        snapshot.HasAlternateKey(entity => new {
                entity.CorrelationId,
                entity.EventId
            })
            .HasName("ak_audit_snapshots_correlation_event");

        snapshot.Property(entity => entity.EventId)
            .HasColumnName("event_id")
            .ValueGeneratedNever();
        snapshot.Property(entity => entity.CorrelationId)
            .HasColumnName("correlation_id");
        snapshot.Property(entity => entity.EventType)
            .HasColumnName("event_type")
            .HasConversion<string>()
            .HasMaxLength(64);
        snapshot.Property(entity => entity.ResponsibleService)
            .HasColumnName("responsible_service")
            .HasMaxLength(200);
        snapshot.Property(entity => entity.Timestamp)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamp with time zone");
        snapshot.Property(entity => entity.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb");
        snapshot.Property(entity => entity.PreviousEventId)
            .HasColumnName("previous_event_id");
        snapshot.Property(entity => entity.Actor)
            .HasColumnName("actor")
            .HasMaxLength(200);
        snapshot.Property(entity => entity.StatusCode)
            .HasColumnName("status_code")
            .HasConversion<string>()
            .HasMaxLength(32);
        snapshot.Property(entity => entity.SequenceNumber)
            .HasColumnName("sequence_number")
            .UseIdentityByDefaultColumn();

        snapshot.HasIndex(entity => entity.SequenceNumber)
            .IsUnique()
            .HasDatabaseName("ux_audit_snapshots_sequence_number");
        snapshot.HasIndex(entity => new {
                entity.CorrelationId,
                entity.Timestamp,
                entity.SequenceNumber
            })
            .HasDatabaseName("ix_audit_snapshots_correlation_timeline");

        snapshot.HasOne<AuditSnapshotEntity>()
            .WithMany()
            .HasForeignKey(
                nameof(AuditSnapshotEntity.CorrelationId),
                nameof(AuditSnapshotEntity.PreviousEventId))
            .HasPrincipalKey(
                nameof(AuditSnapshotEntity.CorrelationId),
                nameof(AuditSnapshotEntity.EventId))
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_audit_snapshots_previous_event");
    }
}
