using System.Text.Json;
using StoreBackend.Application.Ports;
using StoreBackend.Domain;

namespace StoreBackend.Storage;

/// <summary>
/// Vorläufiger Datenbankadapter. Der gesamte Lagerbestand wird als JSON-Datei
/// gelesen und nach jeder erfolgreichen Änderung wieder geschrieben.
/// </summary>
public sealed class JsonWarehouseRepository(
    string dataFilePath,
    ILogger<JsonWarehouseRepository> logger) : IWarehouseRepository {

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
                logger.LogInformation(
                    "Lagerdatei {DataFilePath} mit Startbestand angelegt.",
                    dataFilePath);
                return;
            }

            await using var stream = File.OpenRead(dataFilePath);
            var products = await JsonSerializer.DeserializeAsync<List<WarehouseProduct>>(
                stream,
                JsonOptions,
                cancellationToken) ?? new List<WarehouseProduct>();

            ValidateProducts(products);
            SetProducts(products);

            logger.LogInformation(
                "{ProductCount} Produktdatensätze aus {DataFilePath} geladen.",
                _products.Count,
                dataFilePath);
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

    public async Task<WarehouseProduct?> GetProductAsync(
        Guid productId,
        CancellationToken cancellationToken = default) {

        await _accessLock.WaitAsync(cancellationToken);
        try {
            return _products.GetValueOrDefault(productId);
        }
        finally {
            _accessLock.Release();
        }
    }

    public async Task SaveProductAsync(
        WarehouseProduct product,
        CancellationToken cancellationToken = default) {

        ValidateProducts([product]);

        await _accessLock.WaitAsync(cancellationToken);
        try {
            var updatedProducts = _products.Values
                .Where(current => current.Id != product.Id)
                .Append(product)
                .ToArray();

            // Erst nach erfolgreicher Persistenz wird der sichtbare
            // In-Memory-Bestand übernommen.
            await WriteToDiskCoreAsync(updatedProducts, cancellationToken);
            _products[product.Id] = product;
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

        logger.LogInformation(
            "{ProductCount} Produktdatensätze nach {DataFilePath} geschrieben.",
            productSnapshot.Length,
            dataFilePath);
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
        new(
            Guid.Parse("d63f3cb9-e42e-4d3e-a84d-bfe557e049cc"),
            "Eichenbrett",
            24.95m,
            "",
            12),
        new(
            Guid.Parse("70f70332-945f-4702-9bc2-1cf330f26d42"),
            "Buchenholzplatte",
            39.90m,
            "",
            4),
        new(
            Guid.Parse("5bbdf7bb-9a39-4169-8c9b-e0435741812d"),
            "Nussbaumleiste",
            18.50m,
            "",
            0)
    ];
}
