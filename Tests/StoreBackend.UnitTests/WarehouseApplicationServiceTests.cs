using StoreBackend.Application;
using StoreBackend.Domain;
using StoreBackend.Storage;
using VstOnlineStore.Observability;
using Xunit;

namespace StoreBackend.UnitTests;

public sealed class WarehouseApplicationServiceTests {
    private static readonly Guid ProductId =
        Guid.Parse("d63f3cb9-e42e-4d3e-a84d-bfe557e049cc");

    [Fact]
    public async Task ReserveUndCommitSindPersistentUndIdempotent() {
        using var files = new WarehouseTestFiles();
        var repository = files.CreateRepository();
        await repository.ReadFromDiskAsync();
        var service = new WarehouseApplicationService(repository);
        var reservationId = Guid.NewGuid();
        WarehouseOrderItem[] items = [new(ProductId, 2)];

        var reservation = await service.ReserveProductsAsync(reservationId, items);
        var repeatedReservation = await service.ReserveProductsAsync(reservationId, items);

        Assert.True(reservation.Success);
        Assert.True(repeatedReservation.Success);
        Assert.Equal(10, Assert.Single(repeatedReservation.Products).AvailableQuantity);

        var reloadedRepository = files.CreateRepository();
        await reloadedRepository.ReadFromDiskAsync();
        var reloadedService = new WarehouseApplicationService(reloadedRepository);
        var persistedActiveState = await reloadedRepository.GetStateAsync();
        Assert.Equal(
            WarehouseReservationStatus.ACTIVE,
            Assert.Single(persistedActiveState.Reservations).Status);

        var commit = await reloadedService.CommitProductsAsync(reservationId, items);
        var repeatedCommit = await reloadedService.CommitProductsAsync(reservationId, items);
        var releaseAfterCommit = await reloadedService.ReleaseProductsAsync(reservationId, items);
        var finalState = await reloadedRepository.GetStateAsync();

        Assert.True(commit.Success);
        Assert.True(repeatedCommit.Success);
        Assert.False(releaseAfterCommit.Success);
        Assert.Equal(10, finalState.Products.Single(product => product.Id == ProductId).AvailableQuantity);
        Assert.Equal(
            WarehouseReservationStatus.COMMITTED,
            Assert.Single(finalState.Reservations).Status);
    }

    [Fact]
    public async Task ReleaseStelltBestandNurEinmalWiederHer() {
        using var files = new WarehouseTestFiles();
        var repository = files.CreateRepository();
        await repository.ReadFromDiskAsync();
        var service = new WarehouseApplicationService(repository);
        var reservationId = Guid.NewGuid();
        WarehouseOrderItem[] items = [new(ProductId, 3)];

        var reservation = await service.ReserveProductsAsync(reservationId, items);
        var release = await service.ReleaseProductsAsync(reservationId, items);
        var repeatedRelease = await service.ReleaseProductsAsync(reservationId, items);
        var commitAfterRelease = await service.CommitProductsAsync(reservationId, items);
        var state = await repository.GetStateAsync();

        Assert.True(reservation.Success);
        Assert.True(release.Success);
        Assert.True(repeatedRelease.Success);
        Assert.False(commitAfterRelease.Success);
        Assert.Equal(12, state.Products.Single(product => product.Id == ProductId).AvailableQuantity);
        Assert.Equal(
            WarehouseReservationStatus.RELEASED,
            Assert.Single(state.Reservations).Status);
    }

    private sealed class WarehouseTestFiles : IDisposable {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "VstOnlineStore-StoreBackend-Tests",
            Guid.NewGuid().ToString("N"));

        public JsonWarehouseRepository CreateRepository() =>
            new(
                Path.Combine(_directory, "warehouse-products.json"),
                Path.Combine(_directory, "warehouse-reservations.json"),
                NullStructuredLogger.Instance);

        public void Dispose() {
            if (Directory.Exists(_directory)) {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class NullStructuredLogger : IStructuredLogger {
        public static NullStructuredLogger Instance { get; } = new();

        public void Log(
            StructuredLogLevel logLevel,
            string message,
            object? context = null,
            Exception? exception = null) {
        }
    }
}
