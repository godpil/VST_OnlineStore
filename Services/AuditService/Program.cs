using AuditService.Application;
using AuditService.Application.Ports;
using AuditService.GrpcServices;
using AuditService.Messaging;
using AuditService.Storage;
using Microsoft.EntityFrameworkCore;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddVstOpenTelemetry(
    builder.Configuration,
    "AuditService");
builder.Services.Configure<RabbitMqAuditOptions>(
    builder.Configuration.GetSection(RabbitMqAuditOptions.SectionName));
var auditConnectionString = builder.Configuration.GetConnectionString("AuditDatabase")
    ?? throw new InvalidOperationException(
        "Die Connection-String-Konfiguration 'AuditDatabase' fehlt.");
builder.Services.AddPooledDbContextFactory<AuditDbContext>(options =>
    options.UseNpgsql(auditConnectionString));
builder.Services.AddSingleton<IAuditSnapshotRepository,
    PostgreSqlAuditSnapshotRepository>();
builder.Services.AddSingleton<AuditDatabaseInitializer>();
builder.Services.AddSingleton<AuditApplicationService>();
builder.Services.AddHostedService<RabbitMqAuditEventConsumer>();

var app = builder.Build();

app.UseCorrelationId();
app.UseStructuredRequestLogging();

await app.Services
    .GetRequiredService<AuditDatabaseInitializer>()
    .InitializeAsync();

app.MapGrpcService<AuditOperationsGrpcService>();
app.MapGet("/", () => "AuditService gRPC endpoint");

await app.RunAsync();
