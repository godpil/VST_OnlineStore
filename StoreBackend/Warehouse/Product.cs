namespace StoreBackend.Warehouse {
    public class Product {
        public Guid Id { get; set; }
        public decimal Price { get; set; }
        public string Name { get; set; }
        public string Image { get; set; }
        public bool IsAvailable { get; set; }
        public bool IsReserved { get; set; }

        public Product(
            Guid id,
            decimal price,
            string name,
            bool isAvailable,
            bool isReserved,
            string image = "") {

            Id = id;
            Price = price;
            Name = name;
            Image = image;
            IsAvailable = isAvailable;
            IsReserved = isReserved;
        }
        
        public override string ToString() {
            return $"ProductItem(Id: {Id}, Price: {Price}, Name: {Name}, IsAvailable: {IsAvailable}, IsReserved: {IsReserved})";
        }

        public override bool Equals(object? obj) {
            if (obj is Product other) {
                return Id == other.Id &&
                       Price == other.Price &&
                       Name == other.Name &&
                       Image == other.Image &&
                       IsAvailable == other.IsAvailable &&
                       IsReserved == other.IsReserved;
            }
            return false;
        }

        public override int GetHashCode() {
             return HashCode.Combine(Id, Price, Name, Image, IsAvailable, IsReserved);
        }
    }
}
