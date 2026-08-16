using System.Text.Json;
using StoreBackend.Application.Ports;
using StoreBackend.Domain;
using VstOnlineStore.Observability;

namespace StoreBackend.Storage;

/// <summary>
/// Vorläufiger Datenbankadapter. Der gesamte Lagerbestand wird als JSON-Datei
/// gelesen und nach jeder erfolgreichen Änderung wieder geschrieben.
/// </summary>
public sealed class JsonWarehouseRepository(
    string dataFilePath,
    IStructuredLogger logger) : IWarehouseRepository {

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IgnoreReadOnlyProperties = true
    };

    private readonly SemaphoreSlim _accessLock = new(1, 1);
    private readonly Dictionary<Guid, WarehouseProduct> _products = new();

    /// <summary>
    /// Liest alle Produktdatensätze von der Festplatte. Fehlt die Datei,
    /// wird sie mit einem kleinen Startbestand angelegt.
    /// </summary>
    public async Task ReadFromDiskAsync(CancellationToken cancellationToken = default) {
        await _accessLock.WaitAsync(cancellationToken);
        try {
            if (!File.Exists(dataFilePath)) {
                SetProducts(CreateInitialProducts());
                await WriteToDiskCoreAsync(_products.Values, cancellationToken);
                logger.Info(
                    "Warehouse data file initialized.",
                    new { dataFilePath });
                return;
            }

            await using var stream = File.OpenRead(dataFilePath);
            var products = await JsonSerializer.DeserializeAsync<List<WarehouseProduct>>(
                stream,
                JsonOptions,
                cancellationToken) ?? [];

            ValidateProducts(products);
            SetProducts(products);

            logger.Info(
                "Warehouse data loaded.",
                new {
                    productCount = _products.Count,
                    dataFilePath
                });
        }
        finally {
            _accessLock.Release();
        }
    }

    /// <summary>
    /// Schreibt den aktuellen Lagerbestand auf die Festplatte.
    /// </summary>
    public async Task WriteToDiskAsync(CancellationToken cancellationToken = default) {
        await _accessLock.WaitAsync(cancellationToken);
        try {
            await WriteToDiskCoreAsync(_products.Values, cancellationToken);
        }
        finally {
            _accessLock.Release();
        }
    }

    public async Task<IReadOnlyList<WarehouseProduct>> GetProductsAsync(
        CancellationToken cancellationToken = default) {

        await _accessLock.WaitAsync(cancellationToken);
        try {
            return _products.Values
                .OrderBy(product => product.Name, StringComparer.CurrentCulture)
                .ToArray();
        }
        finally {
            _accessLock.Release();
        }
    }

    public async Task ReplaceProductsAsync(
        IReadOnlyCollection<WarehouseProduct> products,
        CancellationToken cancellationToken = default) {

        ValidateProducts(products);

        await _accessLock.WaitAsync(cancellationToken);
        try {
            // Erst nach erfolgreicher Persistenz wird der sichtbare
            // In-Memory-Bestand übernommen.
            await WriteToDiskCoreAsync(products, cancellationToken);
            SetProducts(products);
        }
        finally {
            _accessLock.Release();
        }
    }

    private async Task WriteToDiskCoreAsync(
        IEnumerable<WarehouseProduct> products,
        CancellationToken cancellationToken) {

        var productSnapshot = products
            .OrderBy(product => product.Name)
            .ToArray();
        var directory = Path.GetDirectoryName(dataFilePath);
        if (!string.IsNullOrWhiteSpace(directory)) {
            Directory.CreateDirectory(directory);
        }

        var temporaryFilePath = $"{dataFilePath}.tmp";
        await using (var stream = File.Create(temporaryFilePath)) {
            await JsonSerializer.SerializeAsync(
                stream,
                productSnapshot,
                JsonOptions,
                cancellationToken);
        }

        File.Move(temporaryFilePath, dataFilePath, overwrite: true);

        logger.Info(
            "Warehouse data written.",
            new {
                productCount = productSnapshot.Length,
                dataFilePath
            });
    }

    private void SetProducts(IEnumerable<WarehouseProduct> products) {
        _products.Clear();
        foreach (var product in products) {
            _products.Add(product.Id, product);
        }
    }

    private static void ValidateProducts(IReadOnlyCollection<WarehouseProduct> products) {
        if (products.Any(product => product.Id == Guid.Empty)) {
            throw new InvalidDataException("Eine Produkt-ID darf nicht leer sein.");
        }

        if (products.Any(product => string.IsNullOrWhiteSpace(product.Name))) {
            throw new InvalidDataException("Ein Produktname darf nicht leer sein.");
        }

        if (products.Any(product => product.Price < 0m)) {
            throw new InvalidDataException("Ein Produktpreis darf nicht negativ sein.");
        }

        if (products.Any(product => product.AvailableQuantity < 0)) {
            throw new InvalidDataException("Ein Lagerbestand darf nicht negativ sein.");
        }

        if (products.Select(product => product.Id).Distinct().Count() != products.Count) {
            throw new InvalidDataException("Produkt-IDs müssen eindeutig sein.");
        }
    }

    private static IReadOnlyList<WarehouseProduct> CreateInitialProducts() => [
        new(Guid.Parse("d63f3cb9-e42e-4d3e-a84d-bfe557e049cc"), "Eichenbrett", 24.95m, "", 12),
        new(Guid.Parse("70f70332-945f-4702-9bc2-1cf330f26d42"), "Buchenholzplatte", 39.90m, "", 4),
        new(Guid.Parse("5bbdf7bb-9a39-4169-8c9b-e0435741812d"), "Nussbaumleiste", 18.50m, "", 0),
        new(Guid.Parse("a1f8c2c0-a2a8-4de0-8ed0-35fcaf7260a1"), "Ahorn-Schneidebrett", 34.50m, "", 7),
        new(Guid.Parse("b2e9d3d1-b3b9-4ef1-9fe1-46fdb08371b2"), "Zirbenholzschale", 44.90m, "", 5),
        new(Guid.Parse("c3fad4e2-c4ca-40a2-a0f2-570ec19482c3"), "Eichen-Servierbrett", 29.90m, "", 9),
        new(Guid.Parse("d40be5f3-d5db-41b3-b103-681fd2a593d4"), "Buchenholz-Hocker", 89.00m, "", 3),
        new(Guid.Parse("e51cf604-e6ec-42c4-c214-7920e3b6a4e5"), "Nussbaum-Messerblock", 74.90m, "", 2)
    ];
}
