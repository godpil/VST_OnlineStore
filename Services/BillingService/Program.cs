using BillingService.GrpcServices;
using BillingService.Payments;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddSingleton<IPaymentProvider, SimulatedPaymentProvider>();
builder.Services.AddVstOpenTelemetry(
    builder.Configuration,
    "BillingService");
builder.Services.AddRabbitMqAuditPublishing(
    builder.Configuration,
    "BillingService");

var app = builder.Build();

app.UseCorrelationId();
app.UseStructuredRequestLogging();
app.MapGrpcService<BillingOperationsGrpcService>();
app.MapGet("/", () => "BillingService gRPC endpoint");

app.Run();
