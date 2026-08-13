namespace StoreBackend.Warehouse {
    public class Warehouse {
        private readonly object _syncRoot = new();

        public List<Product> Products { get; } = new List<Product>();

        public static Warehouse Instance { get; } = new Warehouse();
        private Warehouse() {
            Products.Add(new Product(
                Guid.Parse("d63f3cb9-e42e-4d3e-a84d-bfe557e049cc"),
                24.95m,
                "Eichenbrett",
                isAvailable: true,
                isReserved: false,
                image: "images/eichenbrett.jpg"));
        }

        public IReadOnlyList<Product> GetAvailableProducts() {
            lock (_syncRoot) {
                return Products
                    .Where(product => product.IsAvailable && !product.IsReserved)
                    .ToArray();
            }
        }

        public bool CanSelectProduct(Guid productId) {
            lock (_syncRoot) {
                return Products.Any(product =>
                    product.Id == productId &&
                    product.IsAvailable &&
                    !product.IsReserved);
            }
        }
    }
}
