using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;
using Yarp.ReverseProxy.Transforms;

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
            options.AddPolicy("products", context =>
                CreateFixedWindowPartition(context, permitLimit: 120));
            options.AddPolicy("payment-providers", context =>
                CreateFixedWindowPartition(context, permitLimit: 120));
            options.AddPolicy("orders", context =>
                CreateFixedWindowPartition(context, permitLimit: 10));
            options.AddPolicy("service-statuses", context =>
                CreateFixedWindowPartition(context, permitLimit: 30));
            options.AddPolicy("order-audit-snapshots", context =>
                CreateFixedWindowPartition(context, permitLimit: 30));
            options.AddPolicy("invoice-pdf", context =>
                CreateFixedWindowPartition(context, permitLimit: 30));
            options.OnRejected = WriteRateLimitResponseAsync;
        });
        builder.Services.AddVstOpenTelemetry(
            builder.Configuration,
            "StoreProxy");
        builder.Services.AddRabbitMqAuditPublishing(
            builder.Configuration,
            "StoreProxy");

        var app = builder.Build();

        app.UseCorrelationId();
        app.UseStructuredRequestLogging();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRequestTimeouts();
        app.UseRateLimiter();
        app.UseMiddleware<YarpErrorHandlingMiddleware>();
        app.MapReverseProxy();
        app.Run();
    }

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
