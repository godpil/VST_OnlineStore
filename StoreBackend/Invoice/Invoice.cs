using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace StoreBackend.Invoice {

    public class Invoice {
        private Invoice() { }
        public static Invoice Instance { get; } = new Invoice();
        private List<InvoiceItem> Items { get; set; } = new List<InvoiceItem>(); 

        private PDFBillingResponse CreatePDFBilling(Invoice invoice) {
            var response = new PDFBillingResponse();
            // Logic to create PDF billing goes here
            response.Success = true;
            // TODO: set your license here:
            // QuestPDF.Settings.License = LicenseType.Evaluation;
            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(20));

                    page.Header()
                        .Text("Hello PDF!")
                        .SemiBold().FontSize(36).FontColor(Colors.Blue.Medium);

                    page.Content()
                        .PaddingVertical(1, Unit.Centimetre)
                        .Column(x => {
                            x.Spacing(20);

                            x.Item().Text(Placeholders.LoremIpsum());
                            x.Item().Image(Placeholders.Image(200, 100));
                        });

                    page.Footer()
                        .AlignCenter()
                        .Text(x => {
                            x.Span("Page ");
                            x.CurrentPageNumber();
                        });
                });
            })
            .GeneratePdf("hello.pdf");
            return response;
        }
    }

    internal class PDFBillingResponse {
        public bool Success { get; set; }
        public byte[]? PDFData { get; set; }
    }

}
