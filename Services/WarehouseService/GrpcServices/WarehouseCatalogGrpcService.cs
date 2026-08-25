using Grpc.Core;
using Microsoft.Extensions.Options;
using StoreBackend.Contracts;
using VstOnlineStore.Contracts.WarehouseService;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;
using VstOnlineStore.Presentation;

namespace WarehouseService.GrpcServices;

/// <summary>
/// Öffentliche Warehouse-Grenze. Der Zugriff auf StoreBackend erfolgt
/// ausschließlich über den internen gRPC-Vertrag.
/// </summary>
public sealed class WarehouseCatalogGrpcService(
    WarehouseStorage.WarehouseStorageClient backend,
    IAuditEventPublisher audit,
    IStructuredLogger logger,
    IOptions<PresentationModeOptions> configuredPresentationMode)
    : WarehouseCatalog.WarehouseCatalogBase {

    private readonly PresentationModeOptions _presentationMode =
        configuredPresentationMode.Value;

    public override async Task<FeaturedProductsResponse> GetFeaturedProducts(
        FeaturedProductsRequest request,
        ServerCallContext context) {

        try {
            var backendResponse = await backend.GetProductsAsync(
                new BackendProductsRequest(),
                cancellationToken: context.CancellationToken);
            var response = new FeaturedProductsResponse();

            response.Products.AddRange(
                backendResponse.Products.Select(product => new WarehouseProduct {
                    Id = product.Id,
                    Name = product.Name,
                    PriceInCents = product.PriceInCents,
                    Image = product.Image,
                    AvailableQuantity = product.AvailableQuantity,
                    IsSoldOut = product.IsSoldOut
                }));

            return response;
        }
        catch (Exception exception)
            when (!IsRequestCancellation(exception, context.CancellationToken)) {
            LogBackendFailure(exception, "GetProducts", null, null);
            throw;
        }
    }

    public override Task<CartStockResponse> ReserveCart(
        CartStockRequest request,
        ServerCallContext context) =>
        ChangeReservationAsync(request, StockOperation.Reserve, context.CancellationToken);

    public override Task<CartStockResponse> CommitCart(
        CartStockRequest request,
        ServerCallContext context) =>
        ChangeReservationAsync(request, StockOperation.Commit, context.CancellationToken);

    public override Task<CartStockResponse> ReleaseCart(
        CartStockRequest request,
        ServerCallContext context) =>
        ChangeReservationAsync(request, StockOperation.Release, context.CancellationToken);

    public override async Task<WarehouseStatusResponse> GetStatus(
        WarehouseStatusRequest request,
        ServerCallContext context) {

        try {
            var backendStatus = await backend.GetStatusAsync(
                new BackendStatusRequest(),
                cancellationToken: context.CancellationToken);
            return new WarehouseStatusResponse {
                Available = backendStatus.Available,
                Service = "WarehouseService"
            };
        }
        catch (Exception exception)
            when (!IsRequestCancellation(exception, context.CancellationToken)) {
            LogBackendFailure(exception, "GetStatus", null, null);
            throw;
        }
    }

    private async Task<CartStockResponse> ChangeReservationAsync(
        CartStockRequest request,
        StockOperation operation,
        CancellationToken cancellationToken) {

        var backendRequest = new BackendProductQuantitiesRequest {
            ReservationId = request.ReservationId
        };
        backendRequest.Items.AddRange(request.Items.Select(item => new BackendProductQuantity {
            ProductId = item.ProductId,
            Quantity = item.Quantity
        }));

        BackendStockChangeResponse backendResponse;
        var presentationFailure = _presentationMode.Enabled
            ? GetPresentationFailure(request, operation)
            : null;
        if (presentationFailure is not null) {
            backendResponse = new BackendStockChangeResponse {
                Success = false,
                Message = presentationFailure
            };
            logger.Warn(
                "Warehouse presentation fault injected.",
                new {
                    request.ReservationId,
                    operation = operation.ToString(),
                    presentationScenario = request.PresentationScenario
                });
        }
        else {
            try {
                backendResponse = operation switch {
                    StockOperation.Reserve => await backend.ReserveProductsAsync(
                        backendRequest,
                        cancellationToken: cancellationToken),
                    StockOperation.Commit => await backend.CommitProductsAsync(
                        backendRequest,
                        cancellationToken: cancellationToken),
                    StockOperation.Release => await backend.ReleaseProductsAsync(
                        backendRequest,
                        cancellationToken: cancellationToken),
                    _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
                };
            }
            catch (Exception exception)
                when (!IsRequestCancellation(exception, cancellationToken)) {
                await PublishFailureSnapshotAsync(
                    request,
                    operation,
                    exception,
                    CancellationToken.None);
                LogBackendFailure(
                    exception,
                    GetBackendOperation(operation),
                    operation,
                    request.ReservationId);
                throw;
            }
        }

        var response = new CartStockResponse {
            Success = backendResponse.Success,
            Message = backendResponse.Message
        };
        response.Products.AddRange(backendResponse.Products.Select(product => new CartProductStock {
            ProductId = product.ProductId,
            Name = product.Name,
            PriceInCents = product.PriceInCents,
            AvailableQuantity = product.AvailableQuantity,
            IsSoldOut = product.IsSoldOut
        }));

        await audit.PublishAsync(
            GetEventType(operation),
            "WarehouseService",
            new {
                phase = GetPhase(operation, response.Success),
                operation = operation.ToString().ToUpperInvariant(),
                reservationId = request.ReservationId,
                presentationScenario = NullIfEmpty(request.PresentationScenario),
                success = response.Success,
                response.Message,
                items = request.Items.Select(item => new {
                    item.ProductId,
                    item.Quantity
                }),
                products = response.Products.Select(product => new {
                    product.ProductId,
                    product.AvailableQuantity,
                    product.IsSoldOut
                })
            },
            "WarehouseService",
            GetAuditStatus(operation, response.Success),
            cancellationToken: cancellationToken);

        return response;
    }

    private Task PublishFailureSnapshotAsync(
        CartStockRequest request,
        StockOperation operation,
        Exception exception,
        CancellationToken cancellationToken) =>
        audit.PublishAsync(
            GetEventType(operation),
            "WarehouseService",
            new {
                phase = GetBackendFailurePhase(operation),
                operation = operation.ToString().ToUpperInvariant(),
                reservationId = request.ReservationId,
                presentationScenario = NullIfEmpty(request.PresentationScenario),
                success = false,
                downstreamService = "StoreBackend",
                grpcStatus = (exception as RpcException)?.StatusCode.ToString(),
                exceptionType = exception.GetType().FullName,
                exceptionMessage = exception.Message,
                items = request.Items.Select(item => new {
                    item.ProductId,
                    item.Quantity
                })
            },
            "StoreBackend",
            AuditStatusCode.FAILURE,
            cancellationToken: cancellationToken);

    private void LogBackendFailure(
        Exception exception,
        string operation,
        StockOperation? stockOperation,
        string? reservationId) =>
        logger.Error(
            "Downstream service call failed.",
            new {
                downstreamService = "StoreBackend",
                operation,
                stockOperation = stockOperation?.ToString(),
                reservationId,
                grpcStatus = (exception as RpcException)?.StatusCode.ToString(),
                grpcDetail = (exception as RpcException)?.Status.Detail,
                exceptionType = exception.GetType().FullName,
                exceptionMessage = exception.Message
            },
            exception);

    private static AuditEventType GetEventType(StockOperation operation) =>
        operation == StockOperation.Release
            ? AuditEventType.STOCK_RELEASE
            : AuditEventType.STOCK_RESERVATION;

    private static AuditStatusCode GetAuditStatus(
        StockOperation operation,
        bool success) =>
        !success
            ? AuditStatusCode.FAILURE
            : operation == StockOperation.Release
                ? AuditStatusCode.COMPENSATED
                : AuditStatusCode.SUCCESS;

    private static string GetBackendOperation(StockOperation operation) =>
        operation switch {
            StockOperation.Reserve => "ReserveProducts",
            StockOperation.Commit => "CommitProducts",
            StockOperation.Release => "ReleaseProducts",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

    private static string GetPhase(StockOperation operation, bool success) =>
        operation switch {
            StockOperation.Reserve => success ? "STOCK_RESERVED" : "STOCK_RESERVATION_FAILED",
            StockOperation.Commit => success ? "STOCK_COMMITTED" : "STOCK_COMMIT_FAILED",
            StockOperation.Release => success ? "STOCK_RELEASED" : "STOCK_RELEASE_FAILED",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

    private static string GetBackendFailurePhase(StockOperation operation) =>
        operation switch {
            StockOperation.Reserve => "STORE_BACKEND_RESERVATION_FAILED",
            StockOperation.Commit => "STORE_BACKEND_COMMIT_FAILED",
            StockOperation.Release => "STORE_BACKEND_RELEASE_FAILED",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

    private static string? GetPresentationFailure(
        CartStockRequest request,
        StockOperation operation) {

        if (operation == StockOperation.Reserve &&
            PresentationScenarios.Is(
                request.PresentationScenario,
                PresentationScenarios.OutOfStock)) {
            return "Der Lagerbestand reicht für das Vorführszenario nicht aus.";
        }

        if (operation == StockOperation.Commit &&
            PresentationScenarios.Is(
                request.PresentationScenario,
                PresentationScenarios.WarehouseCommitFailed)) {
            return "Die endgültige Warehouse-Ausbuchung ist im Vorführszenario fehlgeschlagen.";
        }

        return null;
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsRequestCancellation(
        Exception exception,
        CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested &&
        (exception is OperationCanceledException ||
         exception is RpcException { StatusCode: StatusCode.Cancelled });

    private enum StockOperation {
        Reserve,
        Commit,
        Release
    }
}
