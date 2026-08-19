using System.Text.Json;
using InvoiceService.Application;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VstOnlineStore.Messaging;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;

namespace InvoiceService.Messaging;

public sealed class RabbitMqPaymentSucceededEventConsumer(
    IOptions<RabbitMqInvoiceOptions> configuredOptions,
    InvoiceApplicationService applicationService,
    IAuditEventPublisher auditPublisher,
    IStructuredLogger logger) : BackgroundService {

    private const string RoutingKey = "payment.succeeded";
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true
    };

    private readonly RabbitMqInvoiceOptions _options = configuredOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            try {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            }
            catch (Exception exception) {
                logger.Error(
                    "RabbitMQ invoice consumer is unavailable; reconnect will be attempted.",
                    new {
                        rabbitMqHost = _options.HostName,
                        rabbitMqPort = _options.Port,
                        retryAfterSeconds = 5,
                        exceptionType = exception.GetType().FullName,
                        exceptionMessage = exception.Message
                    },
                    exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken) {
        _options.Validate();
        var factory = new ConnectionFactory {
            HostName = _options.HostName,
            Port = _options.Port,
            VirtualHost = _options.VirtualHost,
            UserName = _options.UserName,
            Password = _options.Password,
            ClientProvidedName = "InvoiceService.PaymentConsumer",
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
        await channel.BasicQosAsync(0, 1, false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, eventArgs) =>
            await HandleMessageAsync(channel, eventArgs, stoppingToken);
        await channel.BasicConsumeAsync(
            _options.InvoiceQueue,
            autoAck: false,
            consumer,
            stoppingToken);

        logger.Info(
            "RabbitMQ invoice consumer started.",
            new {
                rabbitMqHost = _options.HostName,
                rabbitMqPort = _options.Port,
                exchange = _options.BillingExchange,
                queue = _options.InvoiceQueue,
                routingKey = RoutingKey
            });
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task HandleMessageAsync(
        IChannel channel,
        BasicDeliverEventArgs eventArgs,
        CancellationToken stoppingToken) {

        PaymentSucceededEvent? paymentEvent = null;
        try {
            paymentEvent = JsonSerializer.Deserialize<PaymentSucceededEvent>(
                eventArgs.Body.Span,
                JsonOptions) ?? throw new JsonException("Die Rechnungsnachricht ist leer.");
            using var correlationScope = CorrelationId.BeginScope(paymentEvent.CorrelationId);

            await applicationService.ProcessPaymentSucceededAsync(
                paymentEvent,
                stoppingToken);
            await channel.BasicAckAsync(eventArgs.DeliveryTag, false, stoppingToken);

            logger.Info(
                "RabbitMQ invoice event processed and acknowledged.",
                new {
                    paymentEvent.EventId,
                    paymentEvent.InvoiceId,
                    eventArgs.DeliveryTag,
                    eventArgs.RoutingKey
                });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Unbestätigte Nachrichten werden nach dem Neustart erneut zugestellt.
        }
        catch (Exception exception) {
            if (paymentEvent is not null && paymentEvent.CorrelationId != Guid.Empty) {
                using var correlationScope = CorrelationId.BeginScope(paymentEvent.CorrelationId);
                logger.Error(
                    "RabbitMQ invoice event failed and is moved to the dead-letter queue.",
                    new {
                        paymentEvent.EventId,
                        paymentEvent.InvoiceId,
                        eventArgs.DeliveryTag,
                        eventArgs.RoutingKey,
                        exceptionType = exception.GetType().FullName,
                        exceptionMessage = exception.Message
                    },
                    exception);
                await auditPublisher.PublishAsync(
                    AuditEventType.INVOICE,
                    "InvoiceService",
                    new {
                        phase = "INVOICE_PROCESSING_FAILED",
                        paymentEvent.EventId,
                        paymentEvent.InvoiceId,
                        paymentEvent.OrderReference,
                        exceptionType = exception.GetType().FullName,
                        exceptionMessage = exception.Message
                    },
                    "InvoiceService",
                    AuditStatusCode.FAILURE,
                    paymentEvent.CorrelationId,
                    CancellationToken.None);
            }
            else {
                logger.Error(
                    "Invalid RabbitMQ invoice event is moved to the dead-letter queue.",
                    new {
                        eventArgs.DeliveryTag,
                        eventArgs.RoutingKey,
                        messageId = eventArgs.BasicProperties.MessageId,
                        correlationId = eventArgs.BasicProperties.CorrelationId,
                        exceptionType = exception.GetType().FullName,
                        exceptionMessage = exception.Message
                    },
                    exception);
            }

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
            _options.BillingExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(
            _options.InvoiceDeadLetterExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            _options.InvoiceDeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            _options.InvoiceDeadLetterQueue,
            _options.InvoiceDeadLetterExchange,
            "#",
            cancellationToken: cancellationToken);

        var queueArguments = new Dictionary<string, object?> {
            ["x-dead-letter-exchange"] = _options.InvoiceDeadLetterExchange
        };
        await channel.QueueDeclareAsync(
            _options.InvoiceQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: queueArguments,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            _options.InvoiceQueue,
            _options.BillingExchange,
            RoutingKey,
            cancellationToken: cancellationToken);
    }
}
