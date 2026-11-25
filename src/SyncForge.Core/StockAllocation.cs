using System.Text.Json.Serialization;

namespace SyncForge.Core;

public sealed record ChannelTarget(string? ListingId, long Version, [property: JsonRequired] int Quantity);
public sealed record AllocationRequest(long Revision, ChannelTarget? ChannelA, ChannelTarget? ChannelB);
public sealed record AllocationPlan(string ListingA, string ListingB, int QuantityA, int QuantityB, Guid? BatchId);
public sealed record AllocationStock(string Sku, int AvailableQuantity, long Revision,
    int? PlannedA, int? PlannedB, Guid? LatestBatchId, bool Active);
public sealed record ChannelListing(string ListingId, string Name, string? Sku, string Channel, int Quantity, long Version);
public sealed record AllocationView(AllocationStock[] Stocks, ChannelListing[] Listings);

public sealed partial class DemoService
{
    // Sessions created before the second channel was introduced also receive its catalog.
    private static bool AddSecondChannel(SessionState state, DateTimeOffset now)
    {
        if (state.Listings.ContainsKey("B-001"))
        {
            return false;
        }

        string[] names = ["Kit de canetas", "Caderno pontilhado", "Estojo de lona"];
        for (var index = 0; index < names.Length; index++)
        {
            var id = $"B-{index + 1:000}";
            state.Listings.Add(id, new Listing { Id = id, Name = names[index], Channel = "B", ObservedAt = now });
        }
        return true;
    }

    private static void SeedTransfer(SessionState state, DateTimeOffset now)
    {
        const string sku = "DEMO-001";
        var a = state.Listings["LIST-001"];
        var b = state.Listings["B-001"];
        a.Sku = b.Sku = sku;
        a.Quantity = 4;
        b.Quantity = 3;
        a.Fault = SimulationFault.LoseResponseAfterApply;
        state.Stocks.Add(sku, new Stock(sku, 12, 5, now, ++state.NextRevision));
        state.Allocations.Add(sku, new AllocationPlan(a.Id, b.Id, 4, 3, null));
    }

    public Task<AllocationView> ReadAllocationsAsync(string sessionId, CancellationToken ct) =>
        transactions.ChangeAsync(sessionId, state =>
        {
            var seeded = AddSecondChannel(state, transactions.Now);
            var busy = ActiveSkus(state);
            var stocks = state.Stocks.Values.OrderBy(stock => stock.Sku, StringComparer.Ordinal).Select(stock =>
            {
                var plan = state.Allocations.GetValueOrDefault(stock.Sku);
                return new AllocationStock(stock.Sku, stock.AvailableQuantity, stock.Revision,
                    plan?.QuantityA, plan?.QuantityB, plan?.BatchId, busy.Contains(stock.Sku));
            }).ToArray();
            var listings = state.Listings.Values.OrderBy(listing => listing.Channel).ThenBy(listing => listing.Id)
                .Select(listing => new ChannelListing(listing.Id, listing.Name, listing.Sku, listing.Channel,
                    listing.Quantity, listing.Version)).ToArray();
            return (new AllocationView(stocks, listings), seeded);
        }, ct);

    private static HashSet<string> ActiveSkus(SessionState state) =>
        state.Batches.Values.SelectMany(batch => batch.Items.Values)
            .Where(item => item.DueAt.HasValue || item.Status == ItemStatus.WaitingForReduction)
            .Select(item => item.Sku).ToHashSet(StringComparer.Ordinal);

    public Task<BatchReceipt> AllocateAsync(string sessionId, string rawSku, AllocationRequest request, string key, CancellationToken ct)
    {
        var sku = StockCsv.NormalizeSku(rawSku) ?? throw new DemoProblem(400, "Informe um SKU válido.");
        if (request.ChannelA is not { Quantity: >= 0, Version: > 0 } targetA ||
            request.ChannelB is not { Quantity: >= 0, Version: > 0 } targetB || request.Revision < 1)
        {
            throw new DemoProblem(400, "Informe os anúncios, as versões e as quantidades inteiras dos dois canais.");
        }

        var fingerprint = Fingerprint("allocate", new { sku, request });
        return transactions.ChangeAsync(sessionId, state =>
        {
            if (ReadPrevious(state, key, fingerprint) is { } previous)
            {
                return (previous, false);
            }
            if (!state.Stocks.TryGetValue(sku, out var stock))
            {
                throw new DemoProblem(404, "SKU não encontrado nesta sessão.");
            }
            if (stock.Revision != request.Revision || ActiveSkus(state).Contains(sku))
            {
                throw new DemoProblem(409, "O SKU mudou ou possui uma correção em andamento. Atualize a distribuição.");
            }
            if ((long)targetA.Quantity + targetB.Quantity > stock.AvailableQuantity)
            {
                throw new DemoProblem(422, "A soma dos canais excede o saldo disponível no ERP.");
            }
            if (state.Batches.Count >= MaxBatches)
            {
                throw new DemoProblem(422, "A sessão atingiu o limite de 16 lotes. Reinicie o cenário para continuar.");
            }

            var now = transactions.Now;
            AddSecondChannel(state, now);
            var a = ReviewedListing(state, sku, "A", targetA);
            var b = ReviewedListing(state, sku, "B", targetB);
            var revision = ++state.NextRevision;
            var batchId = Guid.NewGuid();
            var items = new Dictionary<string, SyncItem>();
            var operationA = Guid.NewGuid();
            var operationB = Guid.NewGuid();
            AddTarget(a, targetA.Quantity, operationA, targetB.Quantity < b.Quantity ? operationB : null);
            AddTarget(b, targetB.Quantity, operationB, targetA.Quantity < a.Quantity ? operationA : null);

            a.Sku = b.Sku = sku;
            state.Stocks[sku] = stock with { Revision = revision };
            state.Allocations[sku] = new AllocationPlan(a.Id, b.Id, targetA.Quantity, targetB.Quantity, batchId);
            state.Batches.Add(batchId, new SyncBatch(batchId, now, items));
            StoreOperation(state, key, fingerprint, batchId);
            return (new BatchReceipt(batchId), true);

            void AddTarget(Listing listing, int quantity, Guid operationId, Guid? otherReduction)
            {
                var increasing = quantity > listing.Quantity;
                var waiting = increasing && otherReduction.HasValue;
                var item = new SyncItem
                {
                    Sku = sku, ListingId = listing.Id, Channel = listing.Channel, DesiredQuantity = quantity,
                    Revision = revision, ExpectedVersion = listing.Version,
                    OperationId = operationId,
                    DependsOn = waiting ? otherReduction : null,
                    DueAt = quantity != listing.Quantity && !waiting ? now : null
                };
                if (waiting)
                {
                    item.Record(now, ItemStatus.WaitingForReduction, "Aguardando a confirmação da redução no outro canal.");
                }
                else if (quantity == listing.Quantity)
                {
                    item.Record(now, ItemStatus.Synchronized, "Saldo já confirmado no anúncio.");
                }
                items.Add(listing.Id, item);
            }
        }, ct);
    }

    private static Listing ReviewedListing(SessionState state, string sku, string channel, ChannelTarget target)
    {
        if (target.ListingId is null || !state.Listings.TryGetValue(target.ListingId, out var listing) || listing.Channel != channel)
        {
            throw new DemoProblem(400, $"Selecione um anúncio do canal {channel}.");
        }
        if (target.Version != listing.Version || (listing.Sku is not null && listing.Sku != sku) ||
            state.Listings.Values.Any(other => other.Channel == channel && other.Sku == sku && other.Id != listing.Id))
        {
            throw new DemoProblem(409, $"O saldo ou o vínculo do canal {channel} mudou. Revise os anúncios.");
        }
        return listing;
    }
}
