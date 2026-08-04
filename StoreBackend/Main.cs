namespace StoreBackend;

public class Program {
    public static void Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        // READ-Kanal des ersten Vertical Slice.
        app.MapGet("/api/products/featured", () => {
            var products = new[]
            {
                new
                {
                    id = 1,
                    name = "Eichenbrett",
                    price = 24.95m,
                    image = "images/eichenbrett.jpg"
                }
            };

            return Results.Ok(products);
        });

        // WRITE-/ACTION-Kanal des ersten Vertical Slice.
        app.MapPost("/api/products/{id:int}/select", (int id, ILogger<Program> logger) => {
            logger.LogInformation(
                "Produkt {ProductId} wurde über die Website ausgewählt.",
                id);

            return Results.Ok(new {
                success = true,
                productId = id
            });
        });

        // Für den ersten lokalen Test bewusst HTTP,
        // damit YARP ohne internes Entwicklungszertifikat testen kann.
        app.Run("http://localhost:6667");
    }
}
