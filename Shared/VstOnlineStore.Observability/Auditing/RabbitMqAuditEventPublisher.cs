using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace VstOnlineStore.Observability.Auditing;

internal sealed record RabbitMqPublisherIdentity(string ServiceName);

/// <summary>
/// Wiederverwendbarer RabbitMQ-Publisher mit Publisher Confirms. Ein Fehler
/// der Audit-Infrastruktur wird strukturiert protokolliert, darf aber niemals
/// den fachlichen Aufruf des sendenden Services abbrechen.
/// </summary>
internal sealed class RabbitMqAuditEventPublisher(
    IOptions<RabbitMqAuditOptions> configuredOptions,
    RabbitMqPublisherIdentity identity,
    IHttpContextAccessor httpContextAccessor,
    IStructuredLogger logger) : IAuditEventPublisher, IAsyncDisposable {

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _channelLock = new(1, 1);
    private readonly RabbitMqAuditOptions _options = configuredOptions.Value;
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(
        AuditEventType eventType,
        string responsibleService,
        object payload,
        string actor,
        AuditStatusCode statusCode,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default) {

        try {
            var effectiveCorrelationId = correlationId ?? GetCurrentCorrelationId();
            if (effectiveCorrelationId == Guid.Empty) {
                throw new InvalidOperationException(
                    "Für den Audit-Snapshot ist keine Correlation-ID verfügbar.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(responsibleService);
            ArgumentNullException.ThrowIfNull(payload);
            ArgumentException.ThrowIfNullOrWhiteSpace(actor);

            var envelope = new AuditEventEnvelope(
                Guid.NewGuid(),
                effectiveCorrelationId,
                eventType,
                responsibleService.Trim(),
                DateTime.UtcNow,
                JsonSerializer.SerializeToElement(payload, JsonOptions),
                actor.Trim(),
                statusCode);
            var body = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

            await _channelLock.WaitAsync(cancellationToken);
            try {
                var channel = await GetOrCreateChannelAsync(cancellationToken);
                var properties = new BasicProperties {
                    AppId = identity.ServiceName,
                    ContentType = "application/json",
                    Persistent = true,
                    MessageId = envelope.EventId.ToString("D"),
                    CorrelationId = envelope.CorrelationId.ToString("D"),
                    Type = nameof(AuditEventEnvelope),
                    Timestamp = new AmqpTimestamp(
                        new DateTimeOffset(envelope.Timestamp).ToUnixTimeSeconds())
                };

                using var confirmationTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                confirmationTimeout.CancelAfter(TimeSpan.FromSeconds(3));

                await channel.BasicPublishAsync(
                    exchange: _options.AuditExchange,
                    routingKey: CreateRoutingKey(envelope.ResponsibleService),
                    mandatory: true,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: confirmationTimeout.Token);
            }
            finally {
                _channelLock.Release();
            }
        }
        catch (Exception exception) {
            TryLogFailure(exception, eventType, responsibleService, correlationId);
        }
    }

    public async ValueTask DisposeAsync() {
        await _channelLock.WaitAsync();
        try {
            if (_channel is not null) {
                await _channel.DisposeAsync();
            }
            if (_connection is not null) {
                await _connection.DisposeAsync();
            }
        }
        finally {
            _channel = null;
            _connection = null;
            _channelLock.Release();
            _channelLock.Dispose();
        }
    }

    private async Task<IChannel> GetOrCreateChannelAsync(
        CancellationToken cancellationToken) {

        _options.Validate();
        if (_connection is null || !_connection.IsOpen) {
            if (_channel is not null) {
                await _channel.DisposeAsync();
                _channel = null;
            }
            if (_connection is not null) {
                await _connection.DisposeAsync();
            }

            var factory = CreateConnectionFactory(_options, identity.ServiceName);
            _connection = await factory.CreateConnectionAsync(cancellationToken);
        }

        if (_channel is null || !_channel.IsOpen) {
            if (_channel is not null) {
                await _channel.DisposeAsync();
            }

            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken);
            await _channel.ExchangeDeclareAsync(
                exchange: _options.AuditExchange,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                cancellationToken: cancellationToken);
        }

        return _channel;
    }

    internal static ConnectionFactory CreateConnectionFactory(
        RabbitMqAuditOptions options,
        string clientName) =>
        new() {
            HostName = options.HostName,
            Port = options.Port,
            VirtualHost = options.VirtualHost,
            UserName = options.UserName,
            Password = options.Password,
            ClientProvidedName = clientName,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(3)
        };

    internal static string CreateRoutingKey(string serviceName) =>
        $"audit.{serviceName.Trim().ToLowerInvariant()}";

    private Guid GetCurrentCorrelationId() {
        var context = httpContextAccessor.HttpContext;
        return context is not null && CorrelationId.TryGet(context, out var correlationId)
            ? correlationId
            : Guid.Empty;
    }

    private void TryLogFailure(
        Exception exception,
        AuditEventType eventType,
        string responsibleService,
        Guid? correlationId) {

        try {
            logger.Error(
                "Audit event could not be published to RabbitMQ.",
                new {
                    eventType = eventType.ToString(),
                    responsibleService,
                    correlationId,
                    rabbitMqHost = _options.HostName,
                    rabbitMqPort = _options.Port,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);
        }
        catch (Exception) {
            // Die Audit-Fehlerbehandlung selbst darf den Geschäftsablauf nicht stören.
        }
    }
}
