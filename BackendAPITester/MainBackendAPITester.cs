using StoreBackend.Warehouse;
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
                Tools.Program.ProgramTools.StartProgram(args);
                TestWarehouse();
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
    }
}