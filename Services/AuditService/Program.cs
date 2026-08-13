using AuditService.GrpcServices;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();

var app = builder.Build();

app.MapGrpcService<AuditOperationsGrpcService>();
app.MapGet("/", () => "AuditService gRPC endpoint");

app.Run();
