using Grpc.Core;
using VstOnlineStore.Observability;
using InvoiceContracts = VstOnlineStore.Contracts.InvoiceService;

namespace ShopService.Queries;

internal static class InvoiceQueryEndpoints {
    public static IEndpointRouteBuilder MapInvoiceQueryEndpoints(
        this IEndpointRouteBuilder endpoints) {

        endpoints.MapGet(
            "/api/invoices/{invoiceId:guid}/pdf",
            GetInvoicePdfAsync);

        return endpoints;
    }

    private static async Task<IResult> GetInvoicePdfAsync(
        Guid invoiceId,
        HttpContext httpContext,
        InvoiceContracts.InvoiceOperations.InvoiceOperationsClient invoices,
        IStructuredLogger logger,
        CancellationToken cancellationToken) {

        try {
            // Die Erstellung läuft asynchron. Das begrenzte Polling bleibt im
            // fachlichen Orchestrator; der StoreProxy leitet nur HTTP weiter.
            for (var attempt = 1; attempt <= 40; attempt++) {
                var response = await invoices.GetInvoicePdfAsync(
                    new InvoiceContracts.GetInvoicePdfRequest {
                        InvoiceId = invoiceId.ToString("D")
                    },
                    deadline: DateTime.UtcNow.AddSeconds(2),
                    cancellationToken: cancellationToken);

                if (response.Found) {
                    var safeFileName = Path.GetFileName(response.FileName)
                        .Replace("\"", string.Empty, StringComparison.Ordinal);
                    httpContext.Response.Headers.ContentDisposition =
                        $"inline; filename=\"{safeFileName}\"";
                    logger.Info(
                        "Invoice PDF delivered to browser.",
                        new {
                            invoiceId,
                            response.FileName,
                            pdfSizeBytes = response.Pdf.Length,
                            attempt
                        });
                    return Results.File(
                        response.Pdf.ToByteArray(),
                        "application/pdf",
                        enableRangeProcessing: true);
                }

                if (attempt < 40) {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                }
            }

            logger.Warn(
                "Invoice PDF was not available within the ShopService wait window.",
                new { invoiceId, waitWindowSeconds = 10 });
            return Results.NotFound(new {
                message = "Die Rechnung wird noch erstellt. Bitte versuchen Sie es erneut.",
                invoiceId
            });
        }
        catch (RpcException exception) when (exception.StatusCode is
            StatusCode.Unavailable or StatusCode.DeadlineExceeded) {
            logger.Warn(
                "Invoice PDF query failed.",
                new { invoiceId, grpcStatus = exception.StatusCode.ToString() },
                exception);
            return Results.Problem(
                title: "InvoiceService nicht erreichbar",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
