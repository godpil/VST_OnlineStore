using AuditService.GrpcServices;
using VstOnlineStore.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddVstOpenTelemetry(
    builder.Configuration,
    "AuditService");

var app = builder.Build();

app.UseCorrelationId();
app.UseStructuredRequestLogging();
app.MapGrpcService<AuditOperationsGrpcService>();
app.MapGet("/", () => "AuditService gRPC endpoint");

app.Run();
