using Grpc.Core;
using VstOnlineStore.Observability;
using InvoiceContracts = VstOnlineStore.Contracts.InvoiceService;

namespace ShopService.Queries;

internal static class InvoiceQueryEndpoints {
    public static IEndpointRouteBuilder MapInvoiceQueryEndpoints(
        this IEndpointRouteBuilder endpoints) {

        endpoints.MapGet(
                "/api/invoices/{invoiceId:guid}/pdf",
                GetInvoicePdfAsync)
            .WithName("GetInvoicePdf")
            .WithTags("Invoices")
            .WithSummary("Rechnung als PDF abrufen")
            .WithDescription(
                "Liefert die erzeugte Rechnung als PDF. Solange die asynchrone Erstellung " +
                "noch läuft, antwortet der Endpunkt mit 404.")
            .Produces<byte[]>(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

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
            return Results.Problem(
                detail: "Die Rechnung wird noch erstellt. Bitte versuchen Sie es erneut.",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?> {
                    ["invoiceId"] = invoiceId
                });
        }
        catch (RpcException exception)
            when (!(cancellationToken.IsCancellationRequested &&
                    exception.StatusCode == StatusCode.Cancelled)) {
            logger.Error(
                "Downstream service call failed.",
                new {
                    downstreamService = "InvoiceService",
                    operation = "GetInvoicePdf",
                    invoiceId,
                    grpcStatus = exception.StatusCode.ToString(),
                    grpcDetail = exception.Status.Detail,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);

            var statusCode = exception.StatusCode switch {
                StatusCode.Unavailable => StatusCodes.Status503ServiceUnavailable,
                StatusCode.DeadlineExceeded => StatusCodes.Status504GatewayTimeout,
                _ => StatusCodes.Status502BadGateway
            };
            return Results.Problem(
                detail: statusCode switch {
                    StatusCodes.Status503ServiceUnavailable =>
                        "Der InvoiceService ist nicht erreichbar.",
                    StatusCodes.Status504GatewayTimeout =>
                        "Der InvoiceService hat nicht rechtzeitig geantwortet.",
                    _ => "Der InvoiceService konnte die Anfrage nicht verarbeiten."
                },
                statusCode: statusCode);
        }
    }
}
