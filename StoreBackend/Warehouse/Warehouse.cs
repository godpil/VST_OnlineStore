namespace StoreBackend.Warehouse {
    public class Warehouse {
        public List<Product> Products { get; set; } = new List<Product>();

        public static Warehouse Instance { get; } = new Warehouse();
        private Warehouse() {
        }
    }
}
