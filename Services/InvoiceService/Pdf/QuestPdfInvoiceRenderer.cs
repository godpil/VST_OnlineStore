using System.Globalization;
using InvoiceService.Application.Ports;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using VstOnlineStore.Messaging;
using VstOnlineStore.Observability;

namespace InvoiceService.Pdf;

public sealed class QuestPdfInvoiceRenderer(
    IStructuredLogger logger) : IInvoicePdfRenderer {

    private static readonly CultureInfo GermanCulture = CultureInfo.GetCultureInfo("de-DE");

    public byte[] Render(PaymentSucceededEvent paymentEvent, string invoiceNumber) {
        ArgumentNullException.ThrowIfNull(paymentEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNumber);

        logger.Info(
            "QuestPDF invoice rendering started.",
            new {
                paymentEvent.InvoiceId,
                invoiceNumber,
                itemCount = paymentEvent.Items.Count,
                paymentEvent.AmountInCents,
                paymentEvent.Currency
            });

        var pdf = Document.Create(document => {
            document.Page(page => {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(style => style.FontSize(10).FontFamily(Fonts.Arial));

                page.Header().Row(row => {
                    row.RelativeItem().Column(column => {
                        column.Item().Text("HOLZWERK ONLINE STORE")
                            .FontSize(20)
                            .SemiBold()
                            .FontColor(Colors.Green.Darken3);
                        column.Item().Text("Rechnung")
                            .FontSize(13)
                            .FontColor(Colors.Grey.Darken1);
                    });

                    row.ConstantItem(170).AlignRight().Column(column => {
                        column.Item().AlignRight().Text(invoiceNumber).SemiBold();
                        column.Item().AlignRight().Text(
                            paymentEvent.PaidAtUtc.ToString("dd.MM.yyyy", GermanCulture));
                    });
                });

                page.Content().PaddingVertical(24).Column(column => {
                    column.Spacing(18);

                    column.Item().Row(row => {
                        row.RelativeItem().Column(address => {
                            address.Item().Text("Rechnungsempfänger").SemiBold();
                            address.Item().Text(paymentEvent.CustomerEmail);
                        });

                        row.RelativeItem().AlignRight().Column(details => {
                            details.Item().AlignRight().Text($"Bestellung: {paymentEvent.OrderReference}");
                            details.Item().AlignRight().Text($"Zahlungsanbieter: {paymentEvent.PaymentProvider}");
                            details.Item().AlignRight().Text($"Transaktion: {paymentEvent.TransactionId}")
                                .FontSize(8)
                                .FontColor(Colors.Grey.Darken1);
                        });
                    });

                    column.Item().Text("Vielen Dank für Ihre Bestellung.");

                    column.Item().Table(table => {
                        table.ColumnsDefinition(columns => {
                            columns.RelativeColumn(5);
                            columns.ConstantColumn(55);
                            columns.ConstantColumn(80);
                            columns.ConstantColumn(85);
                        });

                        table.Header(header => {
                            HeaderCell(header.Cell(), "Artikel");
                            HeaderCell(header.Cell().AlignRight(), "Menge");
                            HeaderCell(header.Cell().AlignRight(), "Einzelpreis");
                            HeaderCell(header.Cell().AlignRight(), "Gesamt");
                        });

                        foreach (var item in paymentEvent.Items) {
                            BodyCell(table.Cell(), item.Description);
                            BodyCell(table.Cell().AlignRight(), item.Quantity.ToString(GermanCulture));
                            BodyCell(
                                table.Cell().AlignRight(),
                                FormatMoney(item.UnitPriceInCents, paymentEvent.Currency));
                            BodyCell(
                                table.Cell().AlignRight(),
                                FormatMoney(
                                    checked(item.UnitPriceInCents * item.Quantity),
                                    paymentEvent.Currency));
                        }

                        table.Cell().ColumnSpan(3).PaddingTop(10).AlignRight()
                            .Text("Rechnungsbetrag").SemiBold();
                        table.Cell().PaddingTop(10).AlignRight()
                            .Text(FormatMoney(paymentEvent.AmountInCents, paymentEvent.Currency))
                            .SemiBold()
                            .FontSize(12)
                            .FontColor(Colors.Green.Darken3);
                    });

                    column.Item()
                        .Background(Colors.Grey.Lighten3)
                        .Padding(12)
                        .Text(
                            $"Der Betrag wurde am {paymentEvent.PaidAtUtc.ToString("dd.MM.yyyy 'um' HH:mm 'Uhr UTC'", GermanCulture)} "
                            + $"über {paymentEvent.PaymentProvider} bezahlt.");
                });

                page.Footer().BorderTop(1).BorderColor(Colors.Grey.Lighten2).PaddingTop(8)
                    .Row(row => {
                        row.RelativeItem().Text("Holzwerk Online Store - Testrechnung")
                            .FontSize(8)
                            .FontColor(Colors.Grey.Darken1);
                        row.ConstantItem(100).AlignRight().Text(text => {
                            text.DefaultTextStyle(style => style.FontSize(8).FontColor(Colors.Grey.Darken1));
                            text.Span("Seite ");
                            text.CurrentPageNumber();
                            text.Span(" / ");
                            text.TotalPages();
                        });
                    });
            });
        }).GeneratePdf();

        logger.Info(
            "QuestPDF invoice rendering completed.",
            new { paymentEvent.InvoiceId, invoiceNumber, pdfSizeBytes = pdf.Length });
        return pdf;
    }

    private static string FormatMoney(long amountInCents, string currency) =>
        $"{amountInCents / 100m:N2} {currency.ToUpperInvariant()}";

    private static void HeaderCell(IContainer container, string text) =>
        container
            .Background(Colors.Green.Darken3)
            .PaddingVertical(7)
            .PaddingHorizontal(6)
            .Text(text)
            .SemiBold()
            .FontColor(Colors.White);

    private static void BodyCell(IContainer container, string text) =>
        container
            .BorderBottom(1)
            .BorderColor(Colors.Grey.Lighten2)
            .PaddingVertical(7)
            .PaddingHorizontal(6)
            .Text(text);
}
