using System.Net.Mail;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using VstOnlineStore.Messaging;
using VstOnlineStore.Observability;

namespace BillingService.Messaging;

public sealed class RabbitMqPaymentSucceededEventPublisher(
    IOptions<RabbitMqInvoiceOptions> configuredOptions,
    IStructuredLogger logger) : IPaymentSucceededEventPublisher, IAsyncDisposable {

    public const string RoutingKey = "payment.succeeded";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RabbitMqInvoiceOptions _options = configuredOptions.Value;
    private readonly SemaphoreSlim _channelLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task<bool> PublishAsync(
        PaymentSucceededEvent paymentEvent,
        CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(paymentEvent);

        try {
            ValidateEvent(paymentEvent);
            var body = JsonSerializer.SerializeToUtf8Bytes(paymentEvent, JsonOptions);

            await _channelLock.WaitAsync(cancellationToken);
            try {
                var channel = await GetOrCreateChannelAsync(cancellationToken);
                var properties = new BasicProperties {
                    AppId = "BillingService",
                    ContentType = "application/json",
                    Persistent = true,
                    MessageId = paymentEvent.EventId.ToString("D"),
                    CorrelationId = paymentEvent.CorrelationId.ToString("D"),
                    Type = nameof(PaymentSucceededEvent),
                    Timestamp = new AmqpTimestamp(
                        new DateTimeOffset(paymentEvent.PaidAtUtc).ToUnixTimeSeconds())
                };

                using var confirmationTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                confirmationTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                await channel.BasicPublishAsync(
                    exchange: _options.BillingExchange,
                    routingKey: RoutingKey,
                    mandatory: true,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: confirmationTimeout.Token);
            }
            finally {
                _channelLock.Release();
            }

            logger.Info(
                "PaymentSucceeded event published for invoice creation.",
                new {
                    paymentEvent.EventId,
                    paymentEvent.InvoiceId,
                    paymentEvent.OrderReference,
                    paymentEvent.PaymentProvider,
                    paymentEvent.TransactionId,
                    recipientDomain = GetRecipientDomain(paymentEvent.CustomerEmail),
                    exchange = _options.BillingExchange,
                    routingKey = RoutingKey
                });
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (Exception exception) {
            logger.Error(
                "PaymentSucceeded event could not be published.",
                new {
                    paymentEvent.EventId,
                    paymentEvent.InvoiceId,
                    paymentEvent.OrderReference,
                    rabbitMqHost = _options.HostName,
                    rabbitMqPort = _options.Port,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);
            return false;
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

            var factory = new ConnectionFactory {
                HostName = _options.HostName,
                Port = _options.Port,
                VirtualHost = _options.VirtualHost,
                UserName = _options.UserName,
                Password = _options.Password,
                ClientProvidedName = "BillingService.InvoicePublisher",
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
                RequestedConnectionTimeout = TimeSpan.FromSeconds(3)
            };
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
                exchange: _options.BillingExchange,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                cancellationToken: cancellationToken);
        }

        return _channel;
    }

    private static void ValidateEvent(PaymentSucceededEvent paymentEvent) {
        if (paymentEvent.EventId == Guid.Empty
            || paymentEvent.InvoiceId == Guid.Empty
            || paymentEvent.CorrelationId == Guid.Empty
            || paymentEvent.PaidAtUtc.Kind != DateTimeKind.Utc
            || paymentEvent.AmountInCents <= 0
            || paymentEvent.Items.Count == 0
            || !MailAddress.TryCreate(paymentEvent.CustomerEmail, out _)) {
            throw new InvalidDataException("Das PaymentSucceeded-Ereignis ist ungültig.");
        }
    }

    private static string? GetRecipientDomain(string email) =>
        MailAddress.TryCreate(email, out var address) ? address.Host : null;
}
