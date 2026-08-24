namespace ShopService.Checkout;

public sealed record CheckoutRequest(
    IReadOnlyList<CheckoutItemRequest>? Items,
    string? CustomerEmail,
    string? PaymentProviderKey);

public sealed record CheckoutItemRequest(string ProductId, int Quantity);

public sealed record CheckoutResponse(
    bool Success,
    string OrderId,
    string Message,
    decimal Total,
    string Currency,
    string? TransactionId,
    string? PaymentProvider,
    string? InvoiceId,
    string? InvoiceUrl);

public sealed record CheckoutOutcome(
    int StatusCode,
    CheckoutResponse Response);
