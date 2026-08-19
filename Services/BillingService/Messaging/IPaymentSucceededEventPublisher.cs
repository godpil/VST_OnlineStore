using VstOnlineStore.Messaging;

namespace BillingService.Messaging;

public interface IPaymentSucceededEventPublisher {
    Task<bool> PublishAsync(
        PaymentSucceededEvent paymentEvent,
        CancellationToken cancellationToken = default);
}
