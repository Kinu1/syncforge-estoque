using Microsoft.Extensions.Logging.Abstractions;
using SyncForge.Core;
using Xunit;

namespace SyncForge.Tests;

public sealed class TransferWorkerTests
{
    [Fact]
    public async Task RecoversLostReductionResponseBeforeReleasingIncrease()
    {
        var transfer = await Transfer.StartAsync();
        var firstWorker = transfer.NewWorker();
        await firstWorker.RunSessionAsync(transfer.SessionId, transfer.Ct);

        var state = await transfer.ReadAsync();
        var reduction = state.Batches[transfer.BatchId].Items["LIST-001"];
        var increase = state.Batches[transfer.BatchId].Items["B-001"];
        Assert.Equal((2, 3), Quantities(state));
        Assert.Equal(ItemStatus.AwaitingConfirmation, reduction.Status);
        Assert.Equal(ItemStatus.WaitingForReduction, increase.Status);
        Assert.Null(increase.DueAt);
        Assert.Equal(0, increase.SendCount);

        transfer.Clock.Advance(5);
        await Task.WhenAll(transfer.NewWorker().RunDueAsync(transfer.Ct), transfer.NewWorker().RunDueAsync(transfer.Ct));

        state = await transfer.ReadAsync();
        reduction = state.Batches[transfer.BatchId].Items["LIST-001"];
        increase = state.Batches[transfer.BatchId].Items["B-001"];
        Assert.Equal((2, 5), Quantities(state));
        Assert.Equal(ItemStatus.Synchronized, reduction.Status);
        Assert.Contains("consulta do ID", reduction.Message);
        Assert.Equal(1, reduction.SendCount);
        Assert.Equal(ItemStatus.Synchronized, increase.Status);
        Assert.Equal(1, increase.SendCount);
        Assert.Equal(2, state.MarketplaceReceipts.Count);
    }

    [Theory]
    [InlineData(SimulationFault.ChangeQuantityBeforeApply, ItemStatus.Conflict)]
    [InlineData(SimulationFault.TransientFailure, ItemStatus.Failed)]
    public async Task BlocksIncreaseWhenReductionCannotBeConfirmed(SimulationFault fault, string finalStatus)
    {
        var transfer = await Transfer.StartAsync();
        await transfer.Transactions.ChangeAsync(transfer.SessionId, state =>
        {
            state.Listings["LIST-001"].Fault = fault;
            return (true, true);
        }, transfer.Ct);
        var worker = transfer.NewWorker();
        await worker.RunDueAsync(transfer.Ct);
        transfer.Clock.Advance(5);
        await worker.RunDueAsync(transfer.Ct);
        transfer.Clock.Advance(30);
        await worker.RunDueAsync(transfer.Ct);

        var state = await transfer.ReadAsync();
        var items = state.Batches[transfer.BatchId].Items;
        Assert.Equal(finalStatus, items["LIST-001"].Status);
        Assert.Equal(ItemStatus.Blocked, items["B-001"].Status);
        Assert.Equal(0, items["B-001"].SendCount);
        Assert.Equal(3, state.Listings["B-001"].Quantity);
        Assert.Null(state.NextDueAt());
    }

    [Fact]
    public async Task NewStockSnapshotPreventsIncreaseAfterLateReceipt()
    {
        var transfer = await Transfer.StartAsync();
        var worker = transfer.NewWorker();
        await worker.RunDueAsync(transfer.Ct);
        transfer.Clock.Advance(5);
        var preview = await transfer.Demo.PreviewAsync(transfer.SessionId, new ImportRequest(
            "sku,physical_quantity,reserved_quantity\nDEMO-001,5,0", transfer.Clock.GetUtcNow()), transfer.Ct);
        await transfer.Demo.ConfirmAsync(transfer.SessionId, preview.Id, transfer.Ct);
        await worker.RunDueAsync(transfer.Ct);

        var state = await transfer.ReadAsync();
        var items = state.Batches[transfer.BatchId].Items;
        Assert.Equal(ItemStatus.Synchronized, items["LIST-001"].Status);
        Assert.Equal(ItemStatus.Superseded, items["B-001"].Status);
        Assert.Equal((2, 3), Quantities(state));
        Assert.Null(state.NextDueAt());
    }

    [Fact]
    public async Task ChecksOtherChannelBalanceAtMomentOfIncrease()
    {
        var transfer = await Transfer.StartAsync();
        var worker = transfer.NewWorker();
        await worker.RunDueAsync(transfer.Ct);
        transfer.Clock.Advance(5);
        var lookup = Assert.IsType<WorkClaim>(await worker.TakeAsync(transfer.SessionId, transfer.Ct));
        await worker.ProcessAsync(transfer.SessionId, lookup, transfer.Ct);
        var increase = Assert.IsType<WorkClaim>(await worker.TakeAsync(transfer.SessionId, transfer.Ct));
        Assert.Equal("B", increase.Item.Channel);

        await transfer.Transactions.ChangeAsync(transfer.SessionId, state =>
        {
            state.Listings["LIST-001"].Quantity = 4;
            state.Listings["LIST-001"].Version++;
            return (true, true);
        }, transfer.Ct);
        await worker.ProcessAsync(transfer.SessionId, increase, transfer.Ct);

        var state = await transfer.ReadAsync();
        Assert.Equal(ItemStatus.Conflict, state.Batches[transfer.BatchId].Items["B-001"].Status);
        Assert.Equal((4, 3), Quantities(state));
    }

    private static (int, int) Quantities(SessionState state) =>
        (state.Listings["LIST-001"].Quantity, state.Listings["B-001"].Quantity);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch.AddYears(56);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }

    private sealed record Transfer(string SessionId, Guid BatchId, DemoService Demo, MemorySessionStore Store,
        SessionTransactions Transactions, ManualClock Clock)
    {
        public CancellationToken Ct => TestContext.Current.CancellationToken;
        public Task<SessionState> ReadAsync() => Transactions.RequireAsync(SessionId, Ct);
        public SyncWorker NewWorker() => new(Store, Transactions, new SimulatedMarketplace(Transactions),
            new NoWorkerWake(), NullLogger<SyncWorker>.Instance);

        public static async Task<Transfer> StartAsync()
        {
            var ct = TestContext.Current.CancellationToken;
            var clock = new ManualClock();
            var store = new MemorySessionStore();
            var transactions = new SessionTransactions(store, clock);
            var demo = new DemoService(store, transactions);
            var id = (await demo.StartAsync(null, ct)).Id;
            await demo.ResetAsync(id, "transfer", ct);
            var state = await transactions.RequireAsync(id, ct);
            var targets = new AllocationRequest(state.Stocks["DEMO-001"].Revision,
                new ChannelTarget("LIST-001", state.Listings["LIST-001"].Version, 2),
                new ChannelTarget("B-001", state.Listings["B-001"].Version, 5));
            var receipt = await demo.AllocateAsync(id, "DEMO-001", targets, "transfer", ct);
            return new Transfer(id, receipt.Id, demo, store, transactions, clock);
        }
    }
}
