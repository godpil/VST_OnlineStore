using InvoiceService.GrpcServices;
using VstOnlineStore.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddVstOpenTelemetry(
    builder.Configuration,
    "InvoiceService");

var app = builder.Build();

app.UseCorrelationId();
app.UseStructuredRequestLogging();
app.MapGrpcService<InvoiceOperationsGrpcService>();
app.MapGet("/", () => "InvoiceService gRPC endpoint");

app.Run();
