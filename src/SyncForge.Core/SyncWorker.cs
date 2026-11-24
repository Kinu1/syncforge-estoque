using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace SyncForge.Core;

public sealed record WorkClaim(Guid BatchId, Guid LeaseToken, SyncItem Item);

public interface IMarketplace
{
    Task<MarketplaceOutcome?> ReadReceiptAsync(string sessionId, Guid operationId, CancellationToken ct);
    Task<MarketplaceOutcome> ApplyAsync(string sessionId, WorkClaim claim, CancellationToken ct);
}

public sealed class SimulatedMarketplace(SessionTransactions transactions) : IMarketplace
{
    public async Task<MarketplaceOutcome?> ReadReceiptAsync(string sessionId, Guid operationId, CancellationToken ct)
    {
        var state = await transactions.RequireAsync(sessionId, ct);
        return state.MarketplaceReceipts.TryGetValue(operationId, out var receipt) ? receipt : null;
    }

    public Task<MarketplaceOutcome> ApplyAsync(string sessionId, WorkClaim claim, CancellationToken ct) =>
        transactions.ChangeAsync(sessionId, state =>
        {
            var operationId = claim.Item.OperationId;
            if (state.MarketplaceReceipts.TryGetValue(operationId, out var previous))
            {
                return (previous, false);
            }

            var item = SyncWorker.FindOwnedItem(state, claim, transactions.Now);
            if (item is null || !SyncWorker.HasCurrentSource(state, item))
            {
                return (MarketplaceOutcome.Unknown, false);
            }

            var listing = state.Listings[item.ListingId];
            if (listing.Fault == SimulationFault.TransientFailure)
            {
                return (MarketplaceOutcome.TransientFailure, false);
            }

            if (listing.Fault == SimulationFault.ChangeQuantityBeforeApply)
            {
                listing.Fault = SimulationFault.None;
                listing.Quantity = listing.Quantity == 0 ? 1 : listing.Quantity - 1;
                listing.Version++;
                listing.ObservedAt = transactions.Now;
            }

            if (listing.Version != item.ExpectedVersion)
            {
                state.MarketplaceReceipts[operationId] = MarketplaceOutcome.VersionConflict;
                return (MarketplaceOutcome.VersionConflict, true);
            }

            listing.Quantity = item.DesiredQuantity;
            listing.Version++;
            listing.ObservedAt = transactions.Now;
            state.MarketplaceReceipts[operationId] = MarketplaceOutcome.Applied;
            if (listing.Fault == SimulationFault.LoseResponseAfterApply)
            {
                listing.Fault = SimulationFault.None;
                return (MarketplaceOutcome.Unknown, true);
            }
            return (MarketplaceOutcome.Applied, true);
        }, ct);
}

public sealed class SyncWorker(
    ISessionStore store,
    SessionTransactions transactions,
    IMarketplace marketplace,
    IWorkerWake wake,
    ILogger<SyncWorker> logger)
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MarketplaceTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];
    private static readonly Meter Meter = new("SyncForge.SyncWorker");
    private static readonly Counter<long> Processed = Meter.CreateCounter<long>("syncforge.items.finais");
    private static readonly Histogram<double> Wait = Meter.CreateHistogram<double>("syncforge.espera.segundos", "s");

    public async Task RunDueAsync(CancellationToken ct)
    {
        var sessions = await store.FindDueAsync(transactions.Now, ct);
        foreach (var sessionId in sessions)
        {
            await RunSessionAsync(sessionId, ct);
        }
    }

    public async Task RunSessionAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var processed = 0;
            for (; processed < StockCsv.MaxRows; processed++)
            {
                var claim = await TakeAsync(sessionId, ct);
                if (claim is null)
                {
                    break;
                }
                await ProcessAsync(sessionId, claim, ct);
            }

            var state = await transactions.RequireAsync(sessionId, ct);
            if (state.NextDueAt() is { } due && due < state.ExpiresAt)
            {
                await wake.NotifyAsync(sessionId, due - transactions.Now, ct);
            }
        }
        catch (DemoProblem problem) when (problem.Status == 401)
        {
            // An expired session can still be visible in the eventually consistent due index.
        }
    }

    public Task<WorkClaim?> TakeAsync(string sessionId, CancellationToken ct) =>
        transactions.ChangeAsync<WorkClaim?>(sessionId, state =>
        {
            var now = transactions.Now;
            var next = state.Batches.Values
                .SelectMany(batch => batch.Items.Values.Select(item => new { BatchId = batch.Id, Item = item }))
                .Where(entry => entry.Item.DueAt <= now)
                .OrderBy(entry => entry.Item.DueAt)
                .ThenBy(entry => entry.Item.Sku, StringComparer.Ordinal)
                .FirstOrDefault();
            if (next is null)
            {
                return (null, false);
            }

            var token = Guid.NewGuid();
            Wait.Record(Math.Max(0, (now - next.Item.DueAt!.Value).TotalSeconds));
            next.Item.LeaseToken = token;
            next.Item.DueAt = now + Lease;
            next.Item.Record(now, ItemStatus.Processing, "Processando atualização de estoque.");
            return (new WorkClaim(next.BatchId, token, next.Item), true);
        }, ct);

    public async Task ProcessAsync(string sessionId, WorkClaim claim, CancellationToken ct)
    {
        var state = await transactions.RequireAsync(sessionId, ct);
        if (FindOwnedItem(state, claim, transactions.Now) is not { } item)
        {
            return;
        }

        var prior = await marketplace.ReadReceiptAsync(sessionId, item.OperationId, ct);
        if (prior is { } receipt)
        {
            await CompleteAsync(sessionId, claim, receipt, confirmed: true, ct);
            return;
        }

        var ready = await transactions.ChangeAsync(sessionId, current =>
        {
            var owned = FindOwnedItem(current, claim, transactions.Now);
            if (owned is null)
            {
                return (false, false);
            }

            if (!HasCurrentSource(current, owned))
            {
                Finish(owned, transactions.Now, ItemStatus.Superseded, "O saldo do ERP ou o vínculo foi atualizado.");
                return (false, true);
            }

            if (owned.SendCount > RetryDelays.Length)
            {
                Finish(owned, transactions.Now, ItemStatus.Failed, "As tentativas foram encerradas sem confirmação.");
                return (false, true);
            }

            owned.SendCount++;
            return (true, true);
        }, ct);
        if (!ready)
        {
            return;
        }

        MarketplaceOutcome outcome;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(MarketplaceTimeout);
        try
        {
            outcome = await marketplace.ApplyAsync(sessionId, claim, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = MarketplaceOutcome.Unknown;
        }

        await CompleteAsync(sessionId, claim, outcome, confirmed: false, ct);
    }

    private async Task CompleteAsync(
        string sessionId, WorkClaim claim, MarketplaceOutcome outcome, bool confirmed, CancellationToken ct)
    {
        var status = await transactions.ChangeAsync<string?>(sessionId, state =>
        {
            var now = transactions.Now;
            var item = FindOwnedItem(state, claim, now);
            if (item is null)
            {
                return (null, false);
            }

            switch (outcome)
            {
                case MarketplaceOutcome.Applied:
                    Finish(item, now, ItemStatus.Synchronized, confirmed
                        ? "Aplicação confirmada pela consulta do ID da operação."
                        : "Saldo aplicado e confirmado pelo marketplace.");
                    break;
                case MarketplaceOutcome.VersionConflict:
                    Finish(item, now, ItemStatus.Conflict, "O saldo do marketplace mudou. Revise a divergência antes de enviar novamente.");
                    break;
                case MarketplaceOutcome.Unknown when !HasCurrentSource(state, item):
                    Finish(item, now, ItemStatus.Superseded, "O saldo do ERP ou o vínculo foi atualizado.");
                    break;
                default:
                    if (item.SendCount > RetryDelays.Length && outcome == MarketplaceOutcome.TransientFailure)
                    {
                        Finish(item, now, ItemStatus.Failed, "O marketplace está indisponível. As tentativas foram encerradas.");
                        break;
                    }

                    var delayIndex = Math.Clamp(item.SendCount - 1, 0, RetryDelays.Length - 1);
                    item.LeaseToken = null;
                    item.DueAt = now + RetryDelays[delayIndex];
                    var message = outcome == MarketplaceOutcome.Unknown
                        ? "O marketplace não confirmou a resposta. A operação será consultada antes de reenviar."
                        : "Marketplace indisponível. Nova tentativa agendada.";
                    item.Record(now, ItemStatus.AwaitingConfirmation, message);
                    break;
            }
            return (item.Status, true);
        }, ct);

        if (status is null)
        {
            return;
        }

        if (status != ItemStatus.AwaitingConfirmation)
        {
            Processed.Add(1, new KeyValuePair<string, object?>("status", status));
        }
        logger.LogInformation(
            "Operação {OperationId} no lote {BatchId} para {Sku}: {Status}",
            claim.Item.OperationId, claim.BatchId, claim.Item.Sku, status);
    }

    private static void Finish(SyncItem item, DateTimeOffset now, string status, string message)
    {
        item.DueAt = null;
        item.LeaseToken = null;
        item.Record(now, status, message);
    }

    public static bool HasCurrentSource(SessionState state, SyncItem item) =>
        state.Stocks.TryGetValue(item.Sku, out var stock) && stock.Revision == item.Revision &&
        state.Listings.TryGetValue(item.ListingId, out var listing) && listing.Sku == item.Sku;

    public static SyncItem? FindOwnedItem(SessionState state, WorkClaim claim, DateTimeOffset now)
    {
        if (state.Batches.TryGetValue(claim.BatchId, out var batch) &&
            batch.Items.TryGetValue(claim.Item.Sku, out var item) &&
            item.LeaseToken == claim.LeaseToken && item.DueAt > now)
        {
            return item;
        }

        return null;
    }
}
