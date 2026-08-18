using InvoiceService.GrpcServices;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddVstOpenTelemetry(
    builder.Configuration,
    "InvoiceService");
builder.Services.AddRabbitMqAuditPublishing(
    builder.Configuration,
    "InvoiceService");

var app = builder.Build();

app.UseCorrelationId();
app.UseStructuredRequestLogging();
app.MapGrpcService<InvoiceOperationsGrpcService>();
app.MapGet("/", () => "InvoiceService gRPC endpoint");

app.Run();
