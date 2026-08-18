using System.Text.Json;
using System.Text.Json.Serialization;
using AuditService.Application;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;
using DomainEventType = AuditService.Domain.AuditEventType;
using DomainStatusCode = AuditService.Domain.AuditStatusCode;
using SharedEventType = VstOnlineStore.Observability.Auditing.AuditEventType;
using SharedStatusCode = VstOnlineStore.Observability.Auditing.AuditStatusCode;

namespace AuditService.Messaging;

/// <summary>
/// Verarbeitet Audit-Ereignisse seriell und bestätigt sie erst nach der
/// erfolgreichen Persistenz. Ungültige Nachrichten werden in die dauerhaft
/// konfigurierte Dead-Letter-Queue verschoben.
/// </summary>
public sealed class RabbitMqAuditEventConsumer(
    IOptions<RabbitMqAuditOptions> configuredOptions,
    AuditApplicationService applicationService,
    IStructuredLogger logger) : BackgroundService {

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly RabbitMqAuditOptions _options = configuredOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            try {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            }
            catch (Exception exception) {
                TryLogConnectionFailure(exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken) {
        ValidateOptions();
        var factory = new ConnectionFactory {
            HostName = _options.HostName,
            Port = _options.Port,
            VirtualHost = _options.VirtualHost,
            UserName = _options.UserName,
            Password = _options.Password,
            ClientProvidedName = "AuditService.Consumer",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(3),
            ConsumerDispatchConcurrency = 1
        };

        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(
            cancellationToken: stoppingToken);

        await DeclareTopologyAsync(channel, stoppingToken);
        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: 1,
            global: false,
            cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, eventArgs) =>
            await HandleMessageAsync(channel, eventArgs, stoppingToken);

        await channel.BasicConsumeAsync(
            queue: _options.AuditQueue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        logger.Info(
            "RabbitMQ audit consumer started.",
            new {
                rabbitMqHost = _options.HostName,
                rabbitMqPort = _options.Port,
                exchange = _options.AuditExchange,
                queue = _options.AuditQueue
            });

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task HandleMessageAsync(
        IChannel channel,
        BasicDeliverEventArgs eventArgs,
        CancellationToken stoppingToken) {

        try {
            // Der vom Client bereitgestellte Speicher ist nur für die Dauer des
            // Callbacks gültig. Die Nachricht wird deshalb sofort kopiert.
            var body = eventArgs.Body.ToArray();
            var envelope = JsonSerializer.Deserialize<AuditEventEnvelope>(body, JsonOptions)
                ?? throw new JsonException("Die Audit-Nachricht ist leer.");

            var snapshot = await applicationService.RecordAsync(
                envelope.EventId,
                envelope.CorrelationId,
                ToDomainEventType(envelope.EventType),
                envelope.ResponsibleService,
                envelope.Timestamp,
                envelope.Payload,
                envelope.Actor,
                ToDomainStatusCode(envelope.StatusCode),
                stoppingToken);

            await channel.BasicAckAsync(
                eventArgs.DeliveryTag,
                multiple: false,
                cancellationToken: stoppingToken);

            logger.Info(
                "RabbitMQ audit event persisted.",
                new {
                    snapshot.EventId,
                    snapshot.CorrelationId,
                    eventType = snapshot.EventType.ToString(),
                    snapshot.ResponsibleService,
                    snapshot.SequenceNumber
                });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Beim Herunterfahren bleibt die Nachricht unbestätigt und wird vom
            // Broker nach dem nächsten Start erneut zugestellt.
        }
        catch (Exception exception) {
            TryLogMessageFailure(exception, eventArgs);
            await channel.BasicNackAsync(
                eventArgs.DeliveryTag,
                multiple: false,
                requeue: false,
                cancellationToken: CancellationToken.None);
        }
    }

    private async Task DeclareTopologyAsync(
        IChannel channel,
        CancellationToken cancellationToken) {

        await channel.ExchangeDeclareAsync(
            exchange: _options.AuditExchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(
            exchange: _options.DeadLetterExchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            queue: _options.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            queue: _options.DeadLetterQueue,
            exchange: _options.DeadLetterExchange,
            routingKey: "#",
            cancellationToken: cancellationToken);

        var queueArguments = new Dictionary<string, object?> {
            ["x-dead-letter-exchange"] = _options.DeadLetterExchange
        };
        await channel.QueueDeclareAsync(
            queue: _options.AuditQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: queueArguments,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            queue: _options.AuditQueue,
            exchange: _options.AuditExchange,
            routingKey: "audit.#",
            cancellationToken: cancellationToken);
    }

    private void ValidateOptions() {
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.HostName);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.Port, 65535);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.VirtualHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.UserName);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.AuditExchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.AuditQueue);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.DeadLetterExchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.DeadLetterQueue);
    }

    private void TryLogConnectionFailure(Exception exception) {
        try {
            logger.Error(
                "RabbitMQ audit consumer is unavailable; reconnect will be attempted.",
                new {
                    rabbitMqHost = _options.HostName,
                    rabbitMqPort = _options.Port,
                    retryAfterSeconds = 5,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);
        }
        catch (Exception) {
            // Die Consumer-Fehlerbehandlung darf den Wiederanlauf nicht verhindern.
        }
    }

    private void TryLogMessageFailure(
        Exception exception,
        BasicDeliverEventArgs eventArgs) {

        try {
            logger.Error(
                "RabbitMQ audit event was rejected and moved to the dead-letter queue.",
                new {
                    eventArgs.RoutingKey,
                    eventArgs.DeliveryTag,
                    messageId = eventArgs.BasicProperties.MessageId,
                    correlationId = eventArgs.BasicProperties.CorrelationId,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);
        }
        catch (Exception) {
            // Siehe TryLogConnectionFailure.
        }
    }

    private static DomainEventType ToDomainEventType(
        SharedEventType eventType) => eventType switch {
            SharedEventType.ORDER_STARTED => DomainEventType.ORDER_STARTED,
            SharedEventType.ORDER_VALIDATED => DomainEventType.ORDER_VALIDATED,
            SharedEventType.STOCK_RESERVATION => DomainEventType.STOCK_RESERVATION,
            SharedEventType.PAYMENT => DomainEventType.PAYMENT,
            SharedEventType.STOCK_RELEASE => DomainEventType.STOCK_RELEASE,
            SharedEventType.ORDER_COMPLETED => DomainEventType.ORDER_COMPLETED,
            _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, null)
        };

    private static DomainStatusCode ToDomainStatusCode(
        SharedStatusCode statusCode) => statusCode switch {
            SharedStatusCode.SUCCESS => DomainStatusCode.SUCCESS,
            SharedStatusCode.FAILURE => DomainStatusCode.FAILURE,
            SharedStatusCode.COMPENSATING => DomainStatusCode.COMPENSATING,
            SharedStatusCode.COMPENSATED => DomainStatusCode.COMPENSATED,
            _ => throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, null)
        };
}
