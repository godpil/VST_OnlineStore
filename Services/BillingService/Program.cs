using BillingService.GrpcServices;
using BillingService.Messaging;
using BillingService.Payments;
using VstOnlineStore.Messaging;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;
using VstOnlineStore.Presentation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddPaymentFacade();
builder.Services
    .AddOptions<PaymentProviderOptions>()
    .Bind(builder.Configuration.GetSection(PaymentProviderOptions.SectionName))
    .Validate(options => options.IsValid(), "Die Payment-Provider-Konfiguration ist ungültig.")
    .ValidateOnStart();
builder.Services.Configure<RabbitMqInvoiceOptions>(
    builder.Configuration.GetSection(RabbitMqInvoiceOptions.SectionName));
builder.Services.Configure<PresentationModeOptions>(
    builder.Configuration.GetSection(PresentationModeOptions.SectionName));
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
