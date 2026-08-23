using Grpc.Core;
using Google.Protobuf;
using InvoiceService.Application;
using VstOnlineStore.Contracts.InvoiceService;
using VstOnlineStore.Observability;

namespace InvoiceService.GrpcServices;

public sealed class InvoiceOperationsGrpcService(
    InvoiceApplicationService applicationService,
    IStructuredLogger logger) : InvoiceOperations.InvoiceOperationsBase {
    public override Task<InvoiceStatusResponse> GetStatus(
        InvoiceStatusRequest request,
        ServerCallContext context) {

        return Task.FromResult(new InvoiceStatusResponse {
            Available = true,
            Service = "InvoiceService"
        });
    }

    public override async Task<GetInvoicePdfResponse> GetInvoicePdf(
        GetInvoicePdfRequest request,
        ServerCallContext context) {

        if (!Guid.TryParse(request.InvoiceId, out var invoiceId)
            || invoiceId == Guid.Empty) {
            logger.Warn(
                "Invoice PDF query rejected invalid input.",
                new {
                    operation = "GetInvoicePdf",
                    reason = "INVALID_INVOICE_ID"
                });
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "Die Rechnungs-ID muss eine gültige GUID sein."));
        }

        Domain.InvoiceRecord? invoice;
        try {
            invoice = await applicationService.GetByIdAsync(
                invoiceId,
                context.CancellationToken);
        }
        catch (Exception exception)
            when (!(context.CancellationToken.IsCancellationRequested &&
                    exception is OperationCanceledException)) {
            logger.Error(
                "Invoice PDF query failed.",
                new {
                    operation = "GetInvoicePdf",
                    invoiceId,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);
            throw;
        }
        if (invoice is null) {
            logger.Debug("Invoice PDF was not found yet.", new { invoiceId });
            return new GetInvoicePdfResponse { Found = false };
        }

        logger.Info(
            "Invoice PDF returned to the requesting upstream service.",
            new {
                invoice.InvoiceId,
                invoice.InvoiceNumber,
                pdfSizeBytes = invoice.PdfDocument.Length
            });
        return new GetInvoicePdfResponse {
            Found = true,
            Pdf = ByteString.CopyFrom(invoice.PdfDocument),
            FileName = $"{invoice.InvoiceNumber}.pdf"
        };
    }
}
