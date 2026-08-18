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
                return new BackendStockChangeResponse {
                    Success = false,
                    Message = "Mindestens eine Produkt-ID ist ungültig."
                };
            }

            items.Add(new WarehouseOrderItem(productId, item.Quantity));
        }

        var result = reserve
            ? await warehouse.ReserveProductsAsync(items, cancellationToken)
            : await warehouse.ReleaseProductsAsync(items, cancellationToken);

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
}
