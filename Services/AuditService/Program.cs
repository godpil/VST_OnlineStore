using AuditService.Application;
using AuditService.Application.Ports;
using AuditService.GrpcServices;
using AuditService.Storage;
using VstOnlineStore.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddVstOpenTelemetry(
    builder.Configuration,
    "AuditService");
builder.Services.AddSingleton<JsonAuditSnapshotRepository>(services => {
    var configuredPath = builder.Configuration["AuditData:FilePath"]
        ?? "Data/audit-snapshots.json";
    var dataFilePath = Path.IsPathRooted(configuredPath)
        ? configuredPath
        : Path.GetFullPath(Path.Combine(
            builder.Environment.ContentRootPath,
            configuredPath));

    return new JsonAuditSnapshotRepository(
        dataFilePath,
        services.GetRequiredService<IStructuredLogger>());
});
builder.Services.AddSingleton<IAuditSnapshotRepository>(services =>
    services.GetRequiredService<JsonAuditSnapshotRepository>());
builder.Services.AddSingleton<AuditApplicationService>();

var app = builder.Build();

app.UseCorrelationId();
app.UseStructuredRequestLogging();

await app.Services
    .GetRequiredService<JsonAuditSnapshotRepository>()
    .ReadFromDiskAsync();

app.MapGrpcService<AuditOperationsGrpcService>();
app.MapGet("/", () => "AuditService gRPC endpoint");

await app.RunAsync();
