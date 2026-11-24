using Microsoft.Extensions.Logging.Abstractions;
using SyncForge.Core;
using Xunit;

namespace SyncForge.Tests;

public sealed class WorkflowTests
{
    [Fact]
    public async Task HandlesConfirmedUnknownAndVersionConflictInSameBatch()
    {
        var system = await SystemUnderTest.StartAsync();
        var id = await system.CreateBatchAsync("mixed", "DEMO-001", "DEMO-002", "DEMO-003");

        await system.Worker.RunSessionAsync(system.Id, system.Ct);
        var statuses = (await system.Demo.ReadBatchAsync(system.Id, id, system.Ct)).Items.Select(item => item.Status);
        Assert.Equal([ItemStatus.Synchronized, ItemStatus.AwaitingConfirmation, ItemStatus.Conflict], statuses);

        system.Clock.Advance(TimeSpan.FromSeconds(5));
        await system.Worker.RunDueAsync(system.Ct);
        var batch = await system.Demo.ReadBatchAsync(system.Id, id, system.Ct);

        Assert.Equal(ItemStatus.Synchronized, batch.Items[1].Status);
        Assert.Contains("consulta do ID", batch.Items[1].Message);
        var refreshed = (await system.Demo.ReadDivergencesAsync(system.Id, system.Ct)).Single(item => item.Sku == "DEMO-003");
        Assert.Equal(5, refreshed.ObservedQuantity);
        Assert.Equal(2L, refreshed.ObservedVersion);
        Assert.Equal("divergent", refreshed.Status);

        var reviewed = await system.CreateBatchAsync("reviewed", "DEMO-003");
        await system.Worker.RunSessionAsync(system.Id, system.Ct);
        Assert.Equal(ItemStatus.Synchronized,
            Assert.Single((await system.Demo.ReadBatchAsync(system.Id, reviewed, system.Ct)).Items).Status);
    }

    [Fact]
    public async Task ReusesIdempotencyKeyForSameSelectionAndRejectsDifferentSelection()
    {
        var system = await SystemUnderTest.StartAsync();
        var selections = await system.SelectionsAsync("DEMO-001", "DEMO-002");

        var receipts = await Task.WhenAll(
            system.Demo.CreateBatchAsync(system.Id, selections, "same-key", system.Ct),
            system.Demo.CreateBatchAsync(system.Id, [.. selections.Reverse()], "same-key", system.Ct));

        Assert.Equal(receipts[0], receipts[1]);
        Assert.Single(await system.Demo.ReadBatchesAsync(system.Id, system.Ct));
        var conflict = await Assert.ThrowsAsync<DemoProblem>(() => system.Demo.CreateBatchAsync(
            system.Id, [selections[0]], "same-key", system.Ct));
        Assert.Equal(409, conflict.Status);
    }

    [Fact]
    public async Task RejectsOlderImportAtomicallyAndSupersedesPendingWorkAfterNewImport()
    {
        var system = await SystemUnderTest.StartAsync();
        var receipt = await system.CreateBatchAsync("old", "DEMO-001");
        var original = await system.Demo.ReadDivergencesAsync(system.Id, system.Ct);

        var older = await system.Demo.PreviewAsync(system.Id, new ImportRequest(
            "sku,physical_quantity,reserved_quantity\nNEW,1,0\nDEMO-001,0,0", system.Clock.GetUtcNow().AddMinutes(-2)), system.Ct);
        var error = await Assert.ThrowsAsync<DemoProblem>(() => system.Demo.ConfirmAsync(system.Id, older.Id, system.Ct));
        Assert.Equal(409, error.Status);
        Assert.Equal(original, await system.Demo.ReadDivergencesAsync(system.Id, system.Ct));

        system.Clock.Advance(TimeSpan.FromSeconds(1));
        var fresh = await system.Demo.PreviewAsync(system.Id, new ImportRequest(
            "sku,physical_quantity,reserved_quantity\nDEMO-001,9,3", system.Clock.GetUtcNow()), system.Ct);
        await system.Demo.ConfirmAsync(system.Id, fresh.Id, system.Ct);
        await system.Worker.RunSessionAsync(system.Id, system.Ct);

        var item = Assert.Single((await system.Demo.ReadBatchAsync(system.Id, receipt, system.Ct)).Items);
        Assert.Equal(ItemStatus.Superseded, item.Status);
        var current = await system.Demo.ReadDivergencesAsync(system.Id, system.Ct);
        Assert.Equal(6, current[0].AvailableQuantity);
        Assert.Equal(original[1], current[1]);
        Assert.Equal(12, current[0].ObservedQuantity);
    }

    [Fact]
    public async Task DoesNotLetExpiredLeaseCompleteAfterAnotherWorkerRecovers()
    {
        var system = await SystemUnderTest.StartAsync();
        var id = await system.CreateBatchAsync("lease", "DEMO-001");
        var oldClaim = Assert.IsType<WorkClaim>(await system.Worker.TakeAsync(system.Id, system.Ct));
        Assert.Null(await system.Worker.TakeAsync(system.Id, system.Ct));

        system.Clock.Advance(TimeSpan.FromSeconds(60));
        var newClaim = Assert.IsType<WorkClaim>(await system.Worker.TakeAsync(system.Id, system.Ct));
        Assert.NotEqual(oldClaim.LeaseToken, newClaim.LeaseToken);
        await system.Worker.ProcessAsync(system.Id, oldClaim, system.Ct);
        await system.Worker.ProcessAsync(system.Id, newClaim, system.Ct);

        var current = (await system.Demo.ReadDivergencesAsync(system.Id, system.Ct))[0];
        Assert.Equal(2L, current.ObservedVersion);
        Assert.Equal(ItemStatus.Synchronized,
            Assert.Single((await system.Demo.ReadBatchAsync(system.Id, id, system.Ct)).Items).Status);
    }

    [Fact]
    public async Task AppliesTwoRetryDelaysAndUsesSameOperationOnManualRetry()
    {
        var system = await SystemUnderTest.StartAsync();
        await system.Transactions.ChangeAsync(system.Id, state =>
        {
            state.Listings["LIST-001"].Fault = SimulationFault.TransientFailure;
            return (true, true);
        }, system.Ct);
        var id = await system.CreateBatchAsync("burst", "DEMO-001");

        var claim = Assert.IsType<WorkClaim>(await system.Worker.TakeAsync(system.Id, system.Ct));
        await system.Worker.ProcessAsync(system.Id, claim, system.Ct);
        Assert.Null(await system.Worker.TakeAsync(system.Id, system.Ct));
        system.Clock.Advance(TimeSpan.FromSeconds(5));
        await system.Worker.RunSessionAsync(system.Id, system.Ct);
        system.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(await system.Worker.TakeAsync(system.Id, system.Ct));
        system.Clock.Advance(TimeSpan.FromSeconds(1));
        await system.Worker.RunSessionAsync(system.Id, system.Ct);

        var failed = Assert.Single((await system.Demo.ReadBatchAsync(system.Id, id, system.Ct)).Items);
        Assert.Equal(ItemStatus.Failed, failed.Status);
        await system.Transactions.ChangeAsync(system.Id, state =>
        {
            state.Listings["LIST-001"].Fault = SimulationFault.None;
            return (true, true);
        }, system.Ct);
        await system.Demo.RetryAsync(system.Id, id, "DEMO-001", "manual", system.Ct);
        var retry = Assert.IsType<WorkClaim>(await system.Worker.TakeAsync(system.Id, system.Ct));
        Assert.Equal(claim.Item.OperationId, retry.Item.OperationId);
        await system.Worker.ProcessAsync(system.Id, retry, system.Ct);
        Assert.Equal(ItemStatus.Synchronized,
            Assert.Single((await system.Demo.ReadBatchAsync(system.Id, id, system.Ct)).Items).Status);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed record SystemUnderTest(
        DemoService Demo, SyncWorker Worker, SessionTransactions Transactions, ManualClock Clock, string Id)
    {
        public CancellationToken Ct => TestContext.Current.CancellationToken;

        public static async Task<SystemUnderTest> StartAsync()
        {
            var ct = TestContext.Current.CancellationToken;
            var store = new MemorySessionStore();
            var clock = new ManualClock();
            var transactions = new SessionTransactions(store, clock);
            var demo = new DemoService(store, transactions);
            var worker = new SyncWorker(store, transactions, new SimulatedMarketplace(transactions),
                new NoWorkerWake(), NullLogger<SyncWorker>.Instance);
            var session = await demo.StartAsync(null, ct);
            await demo.ResetAsync(session.Id, "mixed", ct);
            return new SystemUnderTest(demo, worker, transactions, clock, session.Id);
        }

        public async Task<Guid> CreateBatchAsync(string key, params string[] skus) =>
            (await Demo.CreateBatchAsync(Id, await SelectionsAsync(skus), key, Ct)).Id;

        public async Task<SyncSelection[]> SelectionsAsync(params string[] skus) =>
            (await Demo.ReadDivergencesAsync(Id, Ct)).Where(item => skus.Contains(item.Sku))
                .Select(item => new SyncSelection(item.Sku, item.Revision, item.ObservedVersion)).ToArray();
    }
}
