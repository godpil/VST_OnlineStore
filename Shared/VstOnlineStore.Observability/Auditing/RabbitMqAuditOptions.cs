namespace VstOnlineStore.Observability.Auditing;

public sealed class RabbitMqAuditOptions {
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string AuditExchange { get; set; } = "vst.audit.events";
    public string AuditQueue { get; set; } = "vst.audit.snapshots";
    public string DeadLetterExchange { get; set; } = "vst.audit.dead-letter";
    public string DeadLetterQueue { get; set; } = "vst.audit.snapshots.dead-letter";

    internal void Validate() {
        ArgumentException.ThrowIfNullOrWhiteSpace(HostName);
        ArgumentOutOfRangeException.ThrowIfLessThan(Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Port, 65535);
        ArgumentException.ThrowIfNullOrWhiteSpace(VirtualHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(UserName);
        ArgumentException.ThrowIfNullOrWhiteSpace(AuditExchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(AuditQueue);
        ArgumentException.ThrowIfNullOrWhiteSpace(DeadLetterExchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(DeadLetterQueue);
    }
}
