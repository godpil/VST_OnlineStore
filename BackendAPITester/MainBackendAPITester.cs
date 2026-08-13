using System;
using System.Diagnostics;
using StoreBackend.WarehouseBackend;

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
            var warehouse = new WarehouseBackend();
            // Add some products to the warehouse
            //warehouse.Products.Add(new Product(Guid.NewGuid(), 10.99m, "Product A", true, false));
            //warehouse.Products.Add(new Product(Guid.NewGuid(), 15.49m, "Product B", true, false));
            //warehouse.Products.Add(new Product(Guid.NewGuid(), 7.99m, "Product C", false, true));
            // Display the products in the warehouse
            ConsoleTools.tl("Products in Warehouse:");
            //foreach (var product in warehouse.Products) {
            //ConsoleTools.tl(product.ToString());
            //}

           //warehouse.InsertArticle(Guid.NewGuid(), new (Guid.NewGuid(), 10.99m, "Product A", true, false));
            Article article = new Article(Guid.NewGuid(), 10.99m, "Product A", true, false);
            warehouse.InsertArticle(article.ArticleId, article);


        }

        private static void TestInvoice() {
            //var invoice = Invoice.Instance;
            //var response = invoice.CreatePDFBilling(invoice);
            //if (response.Success) {
                ConsoleTools.tl("PDF billing created successfully.");
            //} else {
                ConsoleTools.tl("Failed to create PDF billing.");
            //}
        }
    }

    internal class WarehouseBackend : IWarehouseBackend {
        public bool IsArticleInStock(Guid articleId) {
            throw new NotImplementedException();
        }
        public IArticle GetArticle(Guid articleId) {
            throw new NotImplementedException();
        }
        public int GetArticleCount(Guid articleId) {
            throw new NotImplementedException();
        }
        public void DeleteArticle(Guid articleId) {
            throw new NotImplementedException();
        }
        public void ReserveArticle(Guid articleId) {
            throw new NotImplementedException();
        }
        public void DereserveArticle(Guid articleId) {
            throw new NotImplementedException();
        }
        public void InsertArticle(Guid articleId, IArticle article) {
            throw new NotImplementedException();
        }
    }

    internal class Article : IArticle {
        public Guid ArticleId { get; set; }
        public decimal Price { get; set; }
        public string Name { get; set; }
        public bool IsInStock { get; set; }
        public bool IsReserved { get; set; }
        public Article(Guid articleId, decimal price, string name, bool isInStock, bool isReserved) {
            ArticleId = articleId;
            Price = price;
            Name = name;
            IsInStock = isInStock;
            IsReserved = isReserved;
        }
        public override string ToString() {
            return $"ArticleId: {ArticleId}, Price: {Price}, Name: {Name}, IsInStock: {IsInStock}, IsReserved: {IsReserved}";
        }
    }
}