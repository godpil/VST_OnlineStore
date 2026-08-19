namespace ShopService.Checkout;

public sealed record CheckoutRequest(
    IReadOnlyList<CheckoutItemRequest>? Items,
    string? PaymentProvider);

public sealed record CheckoutItemRequest(string ProductId, int Quantity);

public sealed record CheckoutResponse(
    bool Success,
    string Message,
    decimal Total,
    string Currency,
    string? TransactionId,
    string? PaymentProvider);

public sealed record CheckoutOutcome(
    int StatusCode,
    CheckoutResponse Response);
