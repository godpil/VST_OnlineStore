using System.Text.Json;
using System.Text.Json.Serialization;
using StoreBackend.Application.Ports;
using StoreBackend.Domain;
using VstOnlineStore.Observability;

namespace StoreBackend.Storage;

/// <summary>
/// Vorläufiger Datenbankadapter. Lagerbestand und Reservierungs-Ledger werden
/// als getrennte JSON-Dateien gelesen und nach jeder Änderung geschrieben.
/// </summary>
public sealed class JsonWarehouseRepository : IWarehouseRepository {

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dataFilePath;
    private readonly string _reservationFilePath;
    private readonly IStructuredLogger _logger;
    private readonly SemaphoreSlim _accessLock = new(1, 1);
    private readonly Dictionary<Guid, WarehouseProduct> _products = new();
    private readonly Dictionary<Guid, WarehouseReservation> _reservations = new();

    public JsonWarehouseRepository(
        string dataFilePath,
        IStructuredLogger logger)
        : this(
            dataFilePath,
            Path.Combine(
                Path.GetDirectoryName(dataFilePath) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(dataFilePath)}.reservations.json"),
            logger) {
    }

    public JsonWarehouseRepository(
        string dataFilePath,
        string reservationFilePath,
        IStructuredLogger logger) {

        _dataFilePath = dataFilePath;
        _reservationFilePath = reservationFilePath;
        _logger = logger;
    }

    /// <summary>
    /// Liest Lagerbestand und Reservierungen von der Festplatte. Fehlt die
    /// Produktdatei, wird sie mit einem kleinen Startbestand angelegt.
    /// </summary>
    public async Task ReadFromDiskAsync(CancellationToken cancellationToken = default) {
        await _accessLock.WaitAsync(cancellationToken);
        try {
            if (!File.Exists(_dataFilePath)) {
                SetProducts(CreateInitialProducts());
                SetReservations([]);
                await WriteStateToDiskCoreAsync(
                    _products.Values,
                    _reservations.Values,
                    cancellationToken);
                _logger.Info(
                    "Warehouse data files initialized.",
                    new {
                        dataFilePath = _dataFilePath,
                        reservationFilePath = _reservationFilePath
                    });
                return;
            }

            await using (var stream = File.OpenRead(_dataFilePath)) {
                var products = await JsonSerializer.DeserializeAsync<List<WarehouseProduct>>(
                    stream,
                    JsonOptions,
                    cancellationToken) ?? [];
                ValidateProducts(products);
                SetProducts(products);
            }

            if (File.Exists(_reservationFilePath)) {
                await using var stream = File.OpenRead(_reservationFilePath);
                var reservations = await JsonSerializer.DeserializeAsync<List<WarehouseReservation>>(
                    stream,
                    JsonOptions,
                    cancellationToken) ?? [];
                ValidateReservations(reservations, _products);
                SetReservations(reservations);
            }
            else {
                SetReservations([]);
            }

            _logger.Info(
                "Warehouse data loaded.",
                new {
                    productCount = _products.Count,
                    reservationCount = _reservations.Count,
                    dataFilePath = _dataFilePath,
                    reservationFilePath = _reservationFilePath
                });
        }
        finally {
            _accessLock.Release();
        }
    }

    public async Task WriteToDiskAsync(CancellationToken cancellationToken = default) {
        await _accessLock.WaitAsync(cancellationToken);
        try {
            await WriteStateToDiskCoreAsync(
                _products.Values,
                _reservations.Values,
                cancellationToken);
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

    public async Task<WarehouseState> GetStateAsync(
        CancellationToken cancellationToken = default) {

        await _accessLock.WaitAsync(cancellationToken);
        try {
            return new WarehouseState(
                _products.Values.ToArray(),
                _reservations.Values.ToArray());
        }
        finally {
            _accessLock.Release();
        }
    }

    public async Task ReplaceStateAsync(
        WarehouseState state,
        CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(state);
        ValidateProducts(state.Products);
        var productsById = state.Products.ToDictionary(product => product.Id);
        ValidateReservations(state.Reservations, productsById);

        await _accessLock.WaitAsync(cancellationToken);
        try {
            // Erst nach erfolgreicher Persistenz wird der sichtbare In-Memory-
            // Zustand übernommen.
            await WriteStateToDiskCoreAsync(
                state.Products,
                state.Reservations,
                cancellationToken);
            SetProducts(state.Products);
            SetReservations(state.Reservations);
        }
        finally {
            _accessLock.Release();
        }
    }

    private async Task WriteStateToDiskCoreAsync(
        IEnumerable<WarehouseProduct> products,
        IEnumerable<WarehouseReservation> reservations,
        CancellationToken cancellationToken) {

        var productSnapshot = products
            .OrderBy(product => product.Name)
            .ToArray();
        var reservationSnapshot = reservations
            .OrderBy(reservation => reservation.CreatedAtUtc)
            .ThenBy(reservation => reservation.ReservationId)
            .ToArray();

        EnsureDirectory(_dataFilePath);
        EnsureDirectory(_reservationFilePath);

        var productTemporaryPath = $"{_dataFilePath}.tmp";
        var reservationTemporaryPath = $"{_reservationFilePath}.tmp";
        try {
            await WriteJsonAsync(
                productTemporaryPath,
                productSnapshot,
                cancellationToken);
            await WriteJsonAsync(
                reservationTemporaryPath,
                reservationSnapshot,
                cancellationToken);

            File.Move(productTemporaryPath, _dataFilePath, overwrite: true);
            File.Move(reservationTemporaryPath, _reservationFilePath, overwrite: true);
        }
        finally {
            TryDeleteTemporaryFile(productTemporaryPath);
            TryDeleteTemporaryFile(reservationTemporaryPath);
        }

        _logger.Info(
            "Warehouse data written.",
            new {
                productCount = productSnapshot.Length,
                reservationCount = reservationSnapshot.Length,
                dataFilePath = _dataFilePath,
                reservationFilePath = _reservationFilePath
            });
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken) {

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream,
            value,
            JsonOptions,
            cancellationToken);
    }

    private static void EnsureDirectory(string path) {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) {
            Directory.CreateDirectory(directory);
        }
    }

    private static void TryDeleteTemporaryFile(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
        catch (IOException) {
            // Eine übrig gebliebene temporäre Datei wird beim nächsten
            // Schreibvorgang überschrieben.
        }
    }

    private void SetProducts(IEnumerable<WarehouseProduct> products) {
        _products.Clear();
        foreach (var product in products) {
            _products.Add(product.Id, product);
        }
    }

    private void SetReservations(IEnumerable<WarehouseReservation> reservations) {
        _reservations.Clear();
        foreach (var reservation in reservations) {
            _reservations.Add(reservation.ReservationId, reservation);
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

    private static void ValidateReservations(
        IReadOnlyCollection<WarehouseReservation> reservations,
        IReadOnlyDictionary<Guid, WarehouseProduct> products) {

        if (reservations.Any(reservation => reservation.ReservationId == Guid.Empty)) {
            throw new InvalidDataException("Eine Reservierungs-ID darf nicht leer sein.");
        }

        if (reservations.Select(reservation => reservation.ReservationId).Distinct().Count()
            != reservations.Count) {
            throw new InvalidDataException("Reservierungs-IDs müssen eindeutig sein.");
        }

        foreach (var reservation in reservations) {
            if (reservation.CreatedAtUtc == default || reservation.Items.Count == 0) {
                throw new InvalidDataException("Eine Reservierung ist unvollständig.");
            }

            if (!Enum.IsDefined(reservation.Status)) {
                throw new InvalidDataException("Der Reservierungsstatus ist ungültig.");
            }

            if (reservation.Items.Any(item =>
                    item.ProductId == Guid.Empty ||
                    item.Quantity <= 0 ||
                    !products.ContainsKey(item.ProductId))) {
                throw new InvalidDataException("Eine Reservierungsposition ist ungültig.");
            }

            if (reservation.Items.Select(item => item.ProductId).Distinct().Count()
                != reservation.Items.Count) {
                throw new InvalidDataException(
                    "Produkt-IDs müssen innerhalb einer Reservierung eindeutig sein.");
            }
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
