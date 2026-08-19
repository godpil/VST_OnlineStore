using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Grpc.Core;
using Microsoft.AspNetCore.RateLimiting;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;
using Yarp.ReverseProxy.Transforms;
using AuditContracts = VstOnlineStore.Contracts.AuditService;

namespace StoreProxy;

public class Program {
    public static void Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services
            .AddReverseProxy()
            .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
            .AddTransforms(transformBuilder =>
                transformBuilder.AddRequestTransform(transformContext => {
                    if (CorrelationId.TryGet(
                        transformContext.HttpContext,
                        out var correlationId)) {
                        transformContext.ProxyRequest.Headers.Remove(CorrelationId.HeaderName);
                        transformContext.ProxyRequest.Headers.TryAddWithoutValidation(
                            CorrelationId.HeaderName,
                            correlationId.ToString("D"));
                    }
                    return ValueTask.CompletedTask;
                }));
        builder.Services.AddRequestTimeouts();
        builder.Services.AddRateLimiter(options => {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("featured-products", context =>
                CreateFixedWindowPartition(context, permitLimit: 120));
            options.AddPolicy("payment-providers", context =>
                CreateFixedWindowPartition(context, permitLimit: 120));
            options.AddPolicy("checkout", context =>
                CreateFixedWindowPartition(context, permitLimit: 10));
            options.AddPolicy("service-status", context =>
                CreateFixedWindowPartition(context, permitLimit: 30));
            options.AddPolicy("audit-orders", context =>
                CreateFixedWindowPartition(context, permitLimit: 30));
            options.OnRejected = WriteRateLimitResponseAsync;
        });
        builder.Services.AddVstOpenTelemetry(
            builder.Configuration,
            "StoreProxy");
        builder.Services.AddRabbitMqAuditPublishing(
            builder.Configuration,
            "StoreProxy");
        builder.Services.AddCorrelationIdPropagation();
        builder.Services.AddGrpcClient<AuditContracts.AuditOperations.AuditOperationsClient>(options => {
            var address = builder.Configuration["Services:AuditService:Address"]
                ?? throw new InvalidOperationException(
                    "Die Konfiguration 'Services:AuditService:Address' fehlt.");
            options.Address = new Uri(address);
        }).AddHttpMessageHandler<CorrelationIdDelegatingHandler>();

        var app = builder.Build();

        app.UseCorrelationId();
        app.UseStructuredRequestLogging();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRequestTimeouts();
        app.UseRateLimiter();
        app.UseMiddleware<YarpErrorHandlingMiddleware>();
        app.MapGet(
            "/audit/orders/{correlationId}",
            GetOrderAuditSnapshotsAsync)
            .WithRequestTimeout(TimeSpan.FromSeconds(5))
            .RequireRateLimiting("audit-orders");
        app.MapReverseProxy();
        app.Run();
    }

    private static async Task<IResult> GetOrderAuditSnapshotsAsync(
        Guid correlationId,
        AuditContracts.AuditOperations.AuditOperationsClient audit,
        IStructuredLogger logger,
        CancellationToken cancellationToken) {

        try {
            var response = await audit.GetOrderSnapshotsAsync(
                new AuditContracts.GetOrderSnapshotsRequest {
                    CorrelationId = correlationId.ToString("D")
                },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: cancellationToken);

            var snapshots = response.Snapshots.Select(snapshot => new {
                eventID = Guid.Parse(snapshot.EventId),
                correlationID = Guid.Parse(snapshot.CorrelationId),
                eventType = ToEventTypeText(snapshot.EventType),
                responsibleService = snapshot.ResponsibleService,
                timestamp = snapshot.Timestamp.ToDateTime(),
                payload = ParsePayload(snapshot.PayloadJson),
                previousEventID = string.IsNullOrWhiteSpace(snapshot.PreviousEventId)
                    ? (Guid?)null
                    : Guid.Parse(snapshot.PreviousEventId),
                actor = snapshot.Actor,
                statusCode = ToStatusCodeText(snapshot.StatusCode)
            }).ToArray();

            return Results.Ok(snapshots);
        }
        catch (RpcException exception) when (exception.StatusCode is
            StatusCode.Unavailable or StatusCode.DeadlineExceeded) {
            logger.Warn(
                "Audit snapshot query failed.",
                new {
                    correlationId,
                    grpcStatus = exception.StatusCode.ToString()
                },
                exception);

            return Results.Problem(
                title: "AuditService nicht erreichbar",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception) when (exception is JsonException or FormatException) {
            logger.Error(
                "Audit snapshot response was invalid.",
                new { correlationId },
                exception);

            return Results.Problem(
                title: "Ungültige Antwort des AuditService",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static JsonElement ParsePayload(string json) {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ToEventTypeText(
        AuditContracts.AuditEventType eventType) => eventType switch {
            AuditContracts.AuditEventType.OrderStarted => "ORDER_STARTED",
            AuditContracts.AuditEventType.OrderValidated => "ORDER_VALIDATED",
            AuditContracts.AuditEventType.StockReservation => "STOCK_RESERVATION",
            AuditContracts.AuditEventType.Payment => "PAYMENT",
            AuditContracts.AuditEventType.StockRelease => "STOCK_RELEASE",
            AuditContracts.AuditEventType.OrderCompleted => "ORDER_COMPLETED",
            _ => throw new JsonException("Der Audit-Event-Typ ist ungültig.")
        };

    private static string ToStatusCodeText(
        AuditContracts.AuditStatusCode statusCode) => statusCode switch {
            AuditContracts.AuditStatusCode.Success => "SUCCESS",
            AuditContracts.AuditStatusCode.Failure => "FAILURE",
            AuditContracts.AuditStatusCode.Compensating => "COMPENSATING",
            AuditContracts.AuditStatusCode.Compensated => "COMPENSATED",
            _ => throw new JsonException("Der Audit-Status ist ungültig.")
        };

    private static RateLimitPartition<string> CreateFixedWindowPartition(
        HttpContext context,
        int permitLimit) {

        var clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown-client";
        return RateLimitPartition.GetFixedWindowLimiter(
            clientKey,
            _ => new FixedWindowRateLimiterOptions {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
                AutoReplenishment = true
            });
    }

    private static async ValueTask WriteRateLimitResponseAsync(
        OnRejectedContext rejectionContext,
        CancellationToken cancellationToken) {

        var context = rejectionContext.HttpContext;
        var correlationId = CorrelationId.TryGet(context, out var currentCorrelationId)
            ? currentCorrelationId
            : Guid.NewGuid();
        var retryAfterSeconds = rejectionContext.Lease.TryGetMetadata(
            MetadataName.RetryAfter,
            out var retryAfter)
                ? retryAfter.TotalSeconds
                : (double?)null;

        if (retryAfterSeconds is not null) {
            context.Response.Headers.RetryAfter = Math.Ceiling(retryAfterSeconds.Value)
                .ToString(CultureInfo.InvariantCulture);
        }

        context.RequestServices
            .GetRequiredService<IStructuredLogger>()
            .Warn(
                "Proxy rate limit exceeded.",
                new {
                    httpMethod = context.Request.Method,
                    path = context.Request.Path.Value ?? "/",
                    client = context.Connection.RemoteIpAddress?.ToString(),
                    retryAfterSeconds
                });

        await context.Response.WriteAsJsonAsync(
            new {
                status = StatusCodes.Status429TooManyRequests,
                message = "Too many requests. Please try again later.",
                correlationID = correlationId
            },
            cancellationToken);
    }
}
