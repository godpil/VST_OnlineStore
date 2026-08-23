using BillingService.GrpcServices;
using BillingService.Messaging;
using BillingService.Payments;
using VstOnlineStore.Messaging;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddSingleton<IPaymentProvider, SimulatedPaymentProvider>();
builder.Services.AddSingleton<IPaymentProvider, PayPalPaymentProvider>();
builder.Services.AddSingleton<IPaymentProvider, StripePaymentProvider>();
builder.Services.Configure<PaymentProviderOptions>(
    builder.Configuration.GetSection(PaymentProviderOptions.SectionName));
builder.Services.AddSingleton<PaymentProviderResolver>();
builder.Services.Configure<RabbitMqInvoiceOptions>(
    builder.Configuration.GetSection(RabbitMqInvoiceOptions.SectionName));
builder.Services.AddSingleton<IPaymentSucceededEventPublisher,
    RabbitMqPaymentSucceededEventPublisher>();
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
