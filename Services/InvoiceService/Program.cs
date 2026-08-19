using InvoiceService.Application;
using InvoiceService.Application.Ports;
using InvoiceService.Email;
using InvoiceService.GrpcServices;
using InvoiceService.Messaging;
using InvoiceService.Pdf;
using InvoiceService.Storage;
using QuestPDF.Infrastructure;
using Microsoft.Extensions.Options;
using VstOnlineStore.Messaging;
using VstOnlineStore.Observability;
using VstOnlineStore.Observability.Auditing;

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.Configure<RabbitMqInvoiceOptions>(
    builder.Configuration.GetSection(RabbitMqInvoiceOptions.SectionName));
builder.Services.Configure<InvoiceEmailOptions>(
    builder.Configuration.GetSection(InvoiceEmailOptions.SectionName));
builder.Services.AddVstOpenTelemetry(
    builder.Configuration,
    "InvoiceService");
builder.Services.AddRabbitMqAuditPublishing(
    builder.Configuration,
    "InvoiceService");
builder.Services.AddSingleton<IInvoiceRepository>(services =>
    new JsonInvoiceRepository(
        Path.Combine(builder.Environment.ContentRootPath, "Data", "invoices.json"),
        services.GetRequiredService<IStructuredLogger>()));
builder.Services.AddSingleton<IInvoicePdfRenderer, QuestPdfInvoiceRenderer>();
builder.Services.AddSingleton<IInvoiceEmailSender>(services =>
{
    var options = services.GetRequiredService<IOptions<InvoiceEmailOptions>>().Value;
    options.Validate();
    if (options.UsesSmtp) {
        return new SmtpInvoiceEmailSender(
            options,
            services.GetRequiredService<IStructuredLogger>());
    }

    var pickupDirectory = Path.IsPathRooted(options.PickupDirectory)
        ? options.PickupDirectory
        : Path.Combine(builder.Environment.ContentRootPath, options.PickupDirectory);
    return new PickupDirectoryInvoiceEmailSender(
        pickupDirectory,
        options.SenderAddress,
        options.SenderName,
        services.GetRequiredService<IStructuredLogger>());
});
builder.Services.AddSingleton<InvoiceApplicationService>();
builder.Services.AddHostedService<RabbitMqPaymentSucceededEventConsumer>();

var app = builder.Build();

await app.Services.GetRequiredService<IInvoiceRepository>().InitializeAsync();

app.UseCorrelationId();
app.UseStructuredRequestLogging();
app.MapGrpcService<InvoiceOperationsGrpcService>();
app.MapGet("/", () => "InvoiceService gRPC endpoint");

app.Run();
