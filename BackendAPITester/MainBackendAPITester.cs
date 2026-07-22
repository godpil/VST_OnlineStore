using StoreBackend.Warehouse;
using StoreBackend.Invoice;
using System;
using System.Diagnostics;

/**/
using Tools.Console;

namespace BackendAPITester
{
    public class MainBackendAPITester
    {
        public static int Main(string[] args)
        {
            byte programCode = 0x0;
            try
            {
                /*
                 * Happypath
                 * 1. Shop-Servcie empfängt Order
                 * 2. Warehouse-Service prüft Lagerbestand
                 * 3. BillingAndPayment sorgt für Auswahl der Zahlungsmethode
                 * 4. Invoice-Service erstellt PDF-Rechnung
                 * 5. Warehouse-Service aktualisiert Lagerbestand
                 * 6. Shop-Service sendet Bestellbestätigung an Kunden (abschluss)
                 */
                Tools.Program.ProgramTools.StartProgram(args);
                
                TestWarehouse();//Artikel im Lager prüfen, reservieren und aktualisieren
                //TestBillingAndPayment();//Zahlungsmethode auswählen und bezahlen
                TestInvoice();//Rechnung generieren
                //TestShopService();//E-Mail

                Tools.Program.ProgramTools.EndProgram(programCode.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }
            return 0;
        }

        private static void TestWarehouse() {
            var warehouse = Warehouse.Instance;
            // Add some products to the warehouse
            warehouse.Products.Add(new Product(Guid.NewGuid(), 10.99m, "Product A", true, false));
            warehouse.Products.Add(new Product(Guid.NewGuid(), 15.49m, "Product B", true, false));
            warehouse.Products.Add(new Product(Guid.NewGuid(), 7.99m, "Product C", false, true));
            // Display the products in the warehouse
            ConsoleTools.tl("Products in Warehouse:");
            foreach (var product in warehouse.Products) {
                ConsoleTools.tl(product.ToString());
            }
            
        }

        private static void TestInvoice() {
            var invoice = Invoice.Instance;
            //var response = invoice.CreatePDFBilling(invoice);
            //if (response.Success) {
                ConsoleTools.tl("PDF billing created successfully.");
            //} else {
                ConsoleTools.tl("Failed to create PDF billing.");
            //}
        }
    }
}