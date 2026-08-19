using VstOnlineStore.Messaging;

namespace InvoiceService.Application.Ports;

public interface IInvoicePdfRenderer {
    byte[] Render(PaymentSucceededEvent paymentEvent, string invoiceNumber);
}
