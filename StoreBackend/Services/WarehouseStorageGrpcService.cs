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

    public override Task<BackendStockChangeResponse> ReserveProducts(
        BackendProductQuantitiesRequest request,
        ServerCallContext context) =>
        ChangeReservationAsync(request, StockOperation.Reserve, context.CancellationToken);

    public override Task<BackendStockChangeResponse> CommitProducts(
        BackendProductQuantitiesRequest request,
        ServerCallContext context) =>
        ChangeReservationAsync(request, StockOperation.Commit, context.CancellationToken);

    public override Task<BackendStockChangeResponse> ReleaseProducts(
        BackendProductQuantitiesRequest request,
        ServerCallContext context) =>
        ChangeReservationAsync(request, StockOperation.Release, context.CancellationToken);

    public override Task<BackendStatusResponse> GetStatus(
        BackendStatusRequest request,
        ServerCallContext context) =>
        Task.FromResult(new BackendStatusResponse {
            Available = true,
            Service = "StoreBackend"
        });

    private async Task<BackendStockChangeResponse> ChangeReservationAsync(
        BackendProductQuantitiesRequest request,
        StockOperation operation,
        CancellationToken cancellationToken) {

        if (!Guid.TryParse(request.ReservationId, out var reservationId) ||
            reservationId == Guid.Empty) {
            return await RejectInvalidInputAsync(
                request,
                operation,
                "Die Reservierungs-ID ist ungültig.",
                "INVALID_RESERVATION_ID",
                cancellationToken);
        }

        var items = new List<WarehouseOrderItem>();
        foreach (var item in request.Items) {
            if (!Guid.TryParse(item.ProductId, out var productId)) {
                return await RejectInvalidInputAsync(
                    request,
                    operation,
                    "Mindestens eine Produkt-ID ist ungültig.",
                    "INVALID_PRODUCT_ID",
                    cancellationToken);
            }

            items.Add(new WarehouseOrderItem(productId, item.Quantity));
        }

        StockChangeResult result;
        try {
            result = operation switch {
                StockOperation.Reserve => await warehouse.ReserveProductsAsync(
                    reservationId,
                    items,
                    cancellationToken),
                StockOperation.Commit => await warehouse.CommitProductsAsync(
                    reservationId,
                    items,
                    cancellationToken),
                StockOperation.Release => await warehouse.ReleaseProductsAsync(
                    reservationId,
                    items,
                    cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            };
        }
        catch (Exception exception)
            when (!IsRequestCancellation(exception, cancellationToken)) {
            logger.Error(
                "Warehouse persistence operation failed.",
                new {
                    operation = GetOperationName(operation),
                    reservationId,
                    productCount = items.Count,
                    exceptionType = exception.GetType().FullName,
                    exceptionMessage = exception.Message
                },
                exception);
            await PublishSnapshotAsync(
                request,
                operation,
                success: false,
                "Die Lageränderung konnte nicht gespeichert werden.",
                GetPersistenceFailurePhase(operation),
                exception,
                CancellationToken.None);
            throw;
        }

        logger.Info(
            "Warehouse reservation state changed.",
            new {
                operation = GetOperationName(operation),
                reservationId,
                productCount = items.Count,
                success = result.Success
            });

        await PublishSnapshotAsync(
            request,
            operation,
            result.Success,
            result.Message,
            GetPersistedPhase(operation),
            null,
            cancellationToken,
            result.Products);

        var response = new BackendStockChangeResponse {
            Success = result.Success,
            Message = result.Message
        };
        response.Products.AddRange(result.Products.Select(product => new BackendProductStock {
            ProductId = product.ProductId.ToString(),
            Name = product.Name,
            PriceInCents = decimal.ToInt64(product.Price * 100m),
            AvailableQuantity = product.AvailableQuantity,
            IsSoldOut = product.IsSoldOut
        }));

        return response;
    }

    private async Task<BackendStockChangeResponse> RejectInvalidInputAsync(
        BackendProductQuantitiesRequest request,
        StockOperation operation,
        string message,
        string phase,
        CancellationToken cancellationToken) {

        logger.Warn(
            "Warehouse reservation change rejected invalid input.",
            new {
                operation = GetOperationName(operation),
                reason = phase,
                request.ReservationId
            });
        await PublishSnapshotAsync(
            request,
            operation,
            success: false,
            message,
            phase,
            null,
            cancellationToken);
        return new BackendStockChangeResponse {
            Success = false,
            Message = message
        };
    }

    private Task PublishSnapshotAsync(
        BackendProductQuantitiesRequest request,
        StockOperation operation,
        bool success,
        string message,
        string phase,
        Exception? exception,
        CancellationToken cancellationToken,
        IReadOnlyList<ProductStock>? products = null) =>
        audit.PublishAsync(
            operation == StockOperation.Release
                ? AuditEventType.STOCK_RELEASE
                : AuditEventType.STOCK_RESERVATION,
            "StoreBackend",
            new {
                phase,
                operation = operation.ToString().ToUpperInvariant(),
                reservationId = request.ReservationId,
                success,
                message,
                exceptionType = exception?.GetType().FullName,
                exceptionMessage = exception?.Message,
                items = request.Items.Select(item => new {
                    item.ProductId,
                    item.Quantity
                }),
                products = products?.Select(product => new {
                    product.ProductId,
                    product.AvailableQuantity,
                    product.IsSoldOut
                })
            },
            "StoreBackend",
            GetAuditStatus(operation, success),
            cancellationToken: cancellationToken);

    private static AuditStatusCode GetAuditStatus(
        StockOperation operation,
        bool success) =>
        !success
            ? AuditStatusCode.FAILURE
            : operation == StockOperation.Release
                ? AuditStatusCode.COMPENSATED
                : AuditStatusCode.SUCCESS;

    private static string GetOperationName(StockOperation operation) =>
        operation switch {
            StockOperation.Reserve => "ReserveProducts",
            StockOperation.Commit => "CommitProducts",
            StockOperation.Release => "ReleaseProducts",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

    private static string GetPersistedPhase(StockOperation operation) =>
        operation switch {
            StockOperation.Reserve => "STOCK_RESERVATION_PERSISTED",
            StockOperation.Commit => "STOCK_COMMIT_PERSISTED",
            StockOperation.Release => "STOCK_RELEASE_PERSISTED",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

    private static string GetPersistenceFailurePhase(StockOperation operation) =>
        operation switch {
            StockOperation.Reserve => "STOCK_RESERVATION_PERSISTENCE_FAILED",
            StockOperation.Commit => "STOCK_COMMIT_PERSISTENCE_FAILED",
            StockOperation.Release => "STOCK_RELEASE_PERSISTENCE_FAILED",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

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
