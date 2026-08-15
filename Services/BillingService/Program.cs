using BillingService.GrpcServices;
using BillingService.Payments;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddSingleton<IPaymentProvider, SimulatedPaymentProvider>();

var app = builder.Build();

app.MapGrpcService<BillingOperationsGrpcService>();
app.MapGet("/", () => "BillingService gRPC endpoint");

app.Run();
