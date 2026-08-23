using Grpc.Core;
using StoreBackend.Contracts;
using VstOnlineStore.Contracts.WarehouseService;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;

namespace WarehouseService.GrpcServices;

/// <summary>
/// Öffentliche Warehouse-Grenze. Der Zugriff auf StoreBackend erfolgt
/// ausschließlich über den internen gRPC-Vertrag.
/// </summary>
public sealed class WarehouseCatalogGrpcService(
    WarehouseStorage.WarehouseStorageClient backend,
    IAuditEventPublisher audit,
    IStructuredLogger logger) : WarehouseCatalog.WarehouseCatalogBase {

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
            LogBackendFailure(exception, "GetProducts", null);
            throw;
        }
    }

    public override async Task<CartStockResponse> ReserveCart(
        CartStockRequest request,
        ServerCallContext context) =>
        await ChangeStockAsync(request, reserve: true, context.CancellationToken);

    public override async Task<CartStockResponse> ReleaseCart(
        CartStockRequest request,
        ServerCallContext context) =>
        await ChangeStockAsync(request, reserve: false, context.CancellationToken);

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
            LogBackendFailure(exception, "GetStatus", null);
            throw;
        }
    }

    private async Task<CartStockResponse> ChangeStockAsync(
        CartStockRequest request,
        bool reserve,
        CancellationToken cancellationToken) {

        var backendRequest = new BackendProductQuantitiesRequest();
        backendRequest.Items.AddRange(request.Items.Select(item => new BackendProductQuantity {
            ProductId = item.ProductId,
            Quantity = item.Quantity
        }));

        BackendStockChangeResponse backendResponse;
        try {
            backendResponse = reserve
                ? await backend.ReserveProductsAsync(backendRequest, cancellationToken: cancellationToken)
                : await backend.ReleaseProductsAsync(backendRequest, cancellationToken: cancellationToken);
        }
        catch (Exception exception)
            when (!IsRequestCancellation(exception, cancellationToken)) {
            await PublishFailureSnapshotAsync(
                request,
                reserve,
                exception,
                CancellationToken.None);
            LogBackendFailure(
                exception,
                reserve ? "ReserveProducts" : "ReleaseProducts",
                reserve);
            throw;
        }

        var response = new CartStockResponse {
            Success = backendResponse.Success,
            Message = backendResponse.Message
        };
        response.Products.AddRange(backendResponse.Products.Select(product => new CartProductStock {
            ProductId = product.ProductId,
            AvailableQuantity = product.AvailableQuantity,
            IsSoldOut = product.IsSoldOut
        }));

        await audit.PublishAsync(
            reserve ? AuditEventType.STOCK_RESERVATION : AuditEventType.STOCK_RELEASE,
            "WarehouseService",
            new {
                phase = reserve ? "STOCK_RESERVATION" : "STOCK_RELEASE",
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
            reserve
                ? response.Success ? AuditStatusCode.SUCCESS : AuditStatusCode.FAILURE
                : response.Success ? AuditStatusCode.COMPENSATED : AuditStatusCode.FAILURE,
            cancellationToken: cancellationToken);

        return response;
    }

    private Task PublishFailureSnapshotAsync(
        CartStockRequest request,
        bool reserve,
        Exception exception,
        CancellationToken cancellationToken) =>
        audit.PublishAsync(
            reserve ? AuditEventType.STOCK_RESERVATION : AuditEventType.STOCK_RELEASE,
            "WarehouseService",
            new {
                phase = reserve
                    ? "STORE_BACKEND_RESERVATION_FAILED"
                    : "STORE_BACKEND_RELEASE_FAILED",
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
        bool? reserve) =>
        logger.Error(
            "Downstream service call failed.",
            new {
                downstreamService = "StoreBackend",
                operation,
                reserve,
                grpcStatus = (exception as RpcException)?.StatusCode.ToString(),
                grpcDetail = (exception as RpcException)?.Status.Detail,
                exceptionType = exception.GetType().FullName,
                exceptionMessage = exception.Message
            },
            exception);

    private static bool IsRequestCancellation(
        Exception exception,
        CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested &&
        (exception is OperationCanceledException ||
         exception is RpcException { StatusCode: StatusCode.Cancelled });
}
