namespace VstOnlineStore.Observability.Auditing;

public interface IAuditEventPublisher {
    /// <summary>
    /// Übergibt einen fachlichen Snapshot an RabbitMQ. Der Aufrufer wartet nur
    /// auf die Annahme durch den Broker, niemals auf den AuditService.
    /// </summary>
    Task PublishAsync(
        AuditEventType eventType,
        string responsibleService,
        object payload,
        string actor,
        AuditStatusCode statusCode,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default);
}
