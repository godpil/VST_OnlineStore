using Grpc.Core;
using StoreBackend.Application;
using StoreBackend.Contracts;
using StoreBackend.Domain;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;

namespace StoreBackend.Services;

/// <summary>
/// Interner gRPC-Transportadapter. Er übersetzt nur zwischen Protobuf und
/// Anwendungsschicht; Geschäfts- und Speicherlogik liegen nicht hier.
/// </summary>
public sealed class WarehouseStorageGrpcService(
    WarehouseApplicationService warehouse,
    IStructuredLogger logger,
    IAuditEventPublisher audit) : WarehouseStorage.WarehouseStorageBase {

    public override async Task<BackendProductsResponse> GetProducts(
        BackendProductsRequest request,
        ServerCallContext context) {

        try {
            var products = await warehouse.GetProductsAsync(context.CancellationToken);
            var response = new BackendProductsResponse();

            response.Products.AddRange(products.Select(product => new StoredProduct {
                Id = product.Id.ToString(),
                Name = product.Name,
                PriceInCents = decimal.ToInt64(product.Price * 100m),
                Image = product.Image,
                AvailableQuantity = product.AvailableQuantity,
                IsSoldOut = product.IsSoldOut
            }));

            return response;
        }
        catch (Exception exception)
            when (!IsRequestCancellation(exception, context.CancellationToken)) {
            logger.Error(
                "Warehouse persistence operation failed.",
                new {
                    operation = "GetProducts",
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);
            throw;
        }
    }

    public override async Task<BackendStockChangeResponse> ReserveProducts(
        BackendProductQuantitiesRequest request,
        ServerCallContext context) =>
        await ChangeStockAsync(request, reserve: true, context.CancellationToken);

    public override async Task<BackendStockChangeResponse> ReleaseProducts(
        BackendProductQuantitiesRequest request,
        ServerCallContext context) =>
        await ChangeStockAsync(request, reserve: false, context.CancellationToken);

    private async Task<BackendStockChangeResponse> ChangeStockAsync(
        BackendProductQuantitiesRequest request,
        bool reserve,
        CancellationToken cancellationToken) {

        var items = new List<WarehouseOrderItem>();
        foreach (var item in request.Items) {
            if (!Guid.TryParse(item.ProductId, out var productId)) {
                logger.Warn(
                    "Warehouse stock change rejected invalid input.",
                    new {
                        operation = reserve ? "ReserveProducts" : "ReleaseProducts",
                        reason = "INVALID_PRODUCT_ID",
                        item.ProductId,
                        item.Quantity
                    });
                await PublishFailureSnapshotAsync(
                    request,
                    reserve,
                    "Mindestens eine Produkt-ID ist ungültig.",
                    "INVALID_PRODUCT_ID",
                    null,
                    cancellationToken);
                return new BackendStockChangeResponse {
                    Success = false,
                    Message = "Mindestens eine Produkt-ID ist ungültig."
                };
            }

            items.Add(new WarehouseOrderItem(productId, item.Quantity));
        }

        StockChangeResult result;
        try {
            result = reserve
                ? await warehouse.ReserveProductsAsync(items, cancellationToken)
                : await warehouse.ReleaseProductsAsync(items, cancellationToken);
        }
        catch (Exception exception)
            when (!IsRequestCancellation(exception, cancellationToken)) {
            logger.Error(
                "Warehouse persistence operation failed.",
                new {
                    operation = reserve ? "ReserveProducts" : "ReleaseProducts",
                    reserve,
                    productCount = items.Count,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);
            await PublishFailureSnapshotAsync(
                request,
                reserve,
                "Die Lageränderung konnte nicht gespeichert werden.",
                reserve
                    ? "STOCK_PERSISTENCE_FAILED"
                    : "STOCK_RELEASE_PERSISTENCE_FAILED",
                exception,
                CancellationToken.None);
            throw;
        }

        logger.Info(
            "Warehouse stock changed.",
            new {
                productCount = items.Count,
                reserve,
                success = result.Success
            });

        await audit.PublishAsync(
            reserve ? AuditEventType.STOCK_RESERVATION : AuditEventType.STOCK_RELEASE,
            "StoreBackend",
            new {
                phase = reserve ? "STOCK_PERSISTED" : "STOCK_RELEASE_PERSISTED",
                reserve,
                success = result.Success,
                result.Message,
                items = items.Select(item => new {
                    item.ProductId,
                    item.Quantity
                }),
                products = result.Products.Select(product => new {
                    product.ProductId,
                    product.AvailableQuantity,
                    product.IsSoldOut
                })
            },
            "StoreBackend",
            reserve
                ? result.Success ? AuditStatusCode.SUCCESS : AuditStatusCode.FAILURE
                : result.Success ? AuditStatusCode.COMPENSATED : AuditStatusCode.FAILURE,
            cancellationToken: cancellationToken);

        var response = new BackendStockChangeResponse {
            Success = result.Success,
            Message = result.Message
        };
        response.Products.AddRange(result.Products.Select(product => new BackendProductStock {
            ProductId = product.ProductId.ToString(),
            AvailableQuantity = product.AvailableQuantity,
            IsSoldOut = product.IsSoldOut
        }));

        return response;
    }

    private Task PublishFailureSnapshotAsync(
        BackendProductQuantitiesRequest request,
        bool reserve,
        string message,
        string phase,
        Exception? exception,
        CancellationToken cancellationToken) =>
        audit.PublishAsync(
            reserve ? AuditEventType.STOCK_RESERVATION : AuditEventType.STOCK_RELEASE,
            "StoreBackend",
            new {
                phase,
                reserve,
                success = false,
                message,
                exceptionType = exception?.GetType().FullName,
                exceptionMessage = exception?.Message,
                items = request.Items.Select(item => new {
                    item.ProductId,
                    item.Quantity
                })
            },
            "StoreBackend",
            AuditStatusCode.FAILURE,
            cancellationToken: cancellationToken);

    private static bool IsRequestCancellation(
        Exception exception,
        CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested &&
        (exception is OperationCanceledException ||
         exception is RpcException { StatusCode: StatusCode.Cancelled });
}
