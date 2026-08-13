using InvoiceService.GrpcServices;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();

var app = builder.Build();

app.MapGrpcService<InvoiceOperationsGrpcService>();
app.MapGet("/", () => "InvoiceService gRPC endpoint");

app.Run();
