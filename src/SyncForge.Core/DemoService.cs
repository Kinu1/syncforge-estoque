using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SyncForge.Core;

public sealed class DemoService(ISessionStore store, SessionTransactions transactions)
{
    private const int MaxBatches = 16;
    private const int MaxImports = 16;
    private const int MaxStocks = 150;
    private const int MaxOperations = 256;
    private static readonly TimeSpan SessionDuration = TimeSpan.FromHours(2);

    public async Task<SessionState> StartAsync(string? sessionId, CancellationToken ct)
    {
        var now = transactions.Now;
        if (sessionId is not null && await store.ReadAsync(sessionId, ct) is { } previous && previous.ExpiresAt > now)
        {
            return previous;
        }

        var state = new SessionState
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            ExpiresAt = now + SessionDuration
        };
        SeedListings(state, now, mixed: false);
        if (!await store.TrySaveAsync(state, expected: null, ct))
        {
            throw new DemoProblem(503, "Não foi possível abrir a sessão. Tente novamente.");
        }
        return state;
    }

    public Task ResetAsync(string sessionId, string? scenario, CancellationToken ct)
    {
        if (scenario != "mixed")
        {
            throw new DemoProblem(400, "Escolha o cenário 'mixed'.");
        }

        return transactions.ChangeAsync(sessionId, state =>
        {
            state.Stocks.Clear();
            state.Imports.Clear();
            state.Listings.Clear();
            state.Batches.Clear();
            state.Operations.Clear();
            state.MarketplaceReceipts.Clear();
            SeedListings(state, transactions.Now, mixed: true);
            return (true, true);
        }, ct);
    }

    public Task<ImportPreview> PreviewAsync(string sessionId, ImportRequest request, CancellationToken ct)
    {
        var now = transactions.Now;
        if (request.CapturedAt is not { } capturedAt || capturedAt > now.AddMinutes(1))
        {
            throw new DemoProblem(400, "Informe uma data de captura válida do ERP, com fuso horário.");
        }

        var preview = StockCsv.Parse(request.Csv);
        return transactions.ChangeAsync(sessionId, state =>
        {
            if (state.Imports.Count >= MaxImports)
            {
                throw new DemoProblem(422, "A sessão atingiu o limite de 16 importações. Reinicie o cenário para continuar.");
            }

            state.Imports.Add(preview.Id, new Import(preview, capturedAt));
            return (preview, true);
        }, ct);
    }

    public Task ConfirmAsync(string sessionId, Guid importId, CancellationToken ct) =>
        transactions.ChangeAsync(sessionId, state =>
        {
            if (!state.Imports.TryGetValue(importId, out var import))
            {
                throw new DemoProblem(404, "Importação não encontrada nesta sessão.");
            }
            if (import.Confirmed)
            {
                return (true, false);
            }
            if (import.Preview.Errors.Length > 0)
            {
                throw new DemoProblem(422, "Corrija os erros do CSV antes de confirmar.");
            }

            if (state.Stocks.Count + import.Preview.Rows.Count(row => !state.Stocks.ContainsKey(row.Sku)) > MaxStocks)
            {
                throw new DemoProblem(422, "A sessão comporta até 150 SKUs.");
            }
            foreach (var row in import.Preview.Rows)
            {
                if (state.Stocks.TryGetValue(row.Sku, out var previous) && previous.CapturedAt >= import.CapturedAt)
                {
                    if (previous.CapturedAt == import.CapturedAt &&
                        previous.PhysicalQuantity == row.PhysicalQuantity &&
                        previous.ReservedQuantity == row.ReservedQuantity)
                    {
                        continue;
                    }
                    throw new DemoProblem(409, $"A captura de {row.Sku} conflita com a versão já confirmada.");
                }

                var revision = ++state.NextRevision;
                state.Stocks[row.Sku] = new Stock(
                    row.Sku, row.PhysicalQuantity, row.ReservedQuantity, import.CapturedAt, revision);
            }

            state.Imports[importId] = import with { Confirmed = true };
            return (true, true);
        }, ct);

    public async Task<DivergenceView[]> ReadDivergencesAsync(string sessionId, CancellationToken ct)
    {
        var state = await transactions.RequireAsync(sessionId, ct);
        var links = state.Listings.Values.Where(item => item.Sku is not null)
            .ToDictionary(item => item.Sku!, StringComparer.Ordinal);
        return state.Stocks.Values.OrderBy(item => item.Sku, StringComparer.Ordinal).Select(stock =>
        {
            links.TryGetValue(stock.Sku, out var listing);
            var status = listing is null ? "unlinked" :
                listing.Quantity == stock.AvailableQuantity ? "aligned" : "divergent";

            return new DivergenceView(
                stock.Sku, stock.PhysicalQuantity, stock.ReservedQuantity, stock.AvailableQuantity,
                listing?.Id, listing?.Quantity, listing?.Version, listing?.ObservedAt, stock.Revision, status);
        }).ToArray();
    }

    public async Task<ListingLinkView[]> ReadLinksAsync(string sessionId, CancellationToken ct)
    {
        var state = await transactions.RequireAsync(sessionId, ct);
        return state.Listings.Values.OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new ListingLinkView(item.Id, item.Name, item.Sku, item.Quantity)).ToArray();
    }

    public Task LinkAsync(string sessionId, string rawSku, string? listingId, CancellationToken ct)
    {
        var sku = StockCsv.NormalizeSku(rawSku) ?? throw new DemoProblem(400, "O SKU informado é inválido.");
        return transactions.ChangeAsync(sessionId, state =>
        {
            if (listingId is null || !state.Listings.TryGetValue(listingId, out var listing) || !state.Stocks.ContainsKey(sku))
            {
                throw new DemoProblem(404, "SKU ou anúncio não encontrado nesta sessão.");
            }
            if (listing.Sku == sku)
            {
                return (true, false);
            }
            if (listing.Sku is not null)
            {
                throw new DemoProblem(409, "O anúncio já está vinculado a outro SKU.");
            }

            var previous = state.Listings.Values.SingleOrDefault(item => item.Sku == sku);
            if (previous is not null)
            {
                previous.Sku = null;
            }
            listing.Sku = sku;
            var stock = state.Stocks[sku];
            state.Stocks[sku] = stock with { Revision = ++state.NextRevision };
            return (true, true);
        }, ct);
    }

    public Task<BatchReceipt> CreateBatchAsync(
        string sessionId, SyncSelection[]? requested, string key, CancellationToken ct)
    {
        if (requested is not { Length: > 0 and <= StockCsv.MaxRows })
        {
            throw new DemoProblem(400, "Selecione entre 1 e 25 divergências.");
        }

        var selections = requested.Select(item =>
        {
            if (item is null || StockCsv.NormalizeSku(item.Sku) is not { } sku ||
                item.Revision < 1 || item.ObservedVersion is null or < 1)
            {
                throw new DemoProblem(400, "Selecione SKUs válidos e informe as versões revisadas.");
            }
            return item with { Sku = sku };
        }).OrderBy(item => item.Sku, StringComparer.Ordinal).ToArray();

        if (selections.Select(item => item.Sku).Distinct(StringComparer.Ordinal).Count() != selections.Length)
        {
            throw new DemoProblem(400, "Selecione cada SKU uma única vez.");
        }

        var fingerprint = Fingerprint("create", selections);
        var id = Guid.NewGuid();
        return transactions.ChangeAsync(sessionId, state =>
        {
            if (ReadPrevious(state, key, fingerprint) is { } previous)
            {
                return (previous, false);
            }
            if (state.Batches.Count >= MaxBatches)
            {
                throw new DemoProblem(422, "A sessão atingiu o limite de 16 lotes. Reinicie o cenário para continuar.");
            }

            var now = transactions.Now;
            var items = new Dictionary<string, SyncItem>(StringComparer.Ordinal);
            foreach (var selection in selections)
            {
                var sku = selection.Sku!;
                var listing = state.Listings.Values.SingleOrDefault(item => item.Sku == sku);
                if (!state.Stocks.TryGetValue(sku, out var stock) || stock.Revision != selection.Revision ||
                    listing is null || listing.Version != selection.ObservedVersion || listing.Quantity == stock.AvailableQuantity)
                {
                    throw new DemoProblem(409, $"A divergência de {sku} mudou. Revise as informações antes de criar o lote.");
                }
                if (state.Batches.Values.Any(batch => batch.Items.TryGetValue(sku, out var item) &&
                    item.Revision == selection.Revision && item.ExpectedVersion == selection.ObservedVersion &&
                    item.DueAt is not null))
                {
                    throw new DemoProblem(409, $"Já existe uma sincronização em andamento para {sku}.");
                }

                items[sku] = new SyncItem
                {
                    Sku = sku,
                    ListingId = listing.Id,
                    DesiredQuantity = stock.AvailableQuantity,
                    Revision = stock.Revision,
                    ExpectedVersion = listing.Version,
                    OperationId = Guid.NewGuid(),
                    DueAt = now
                };
            }

            StoreOperation(state, key, fingerprint, id);
            state.Batches.Add(id, new SyncBatch(id, now, items));
            return (new BatchReceipt(id), true);
        }, ct);
    }

    public Task<BatchReceipt> RetryAsync(string sessionId, Guid id, string rawSku, string key, CancellationToken ct)
    {
        var sku = StockCsv.NormalizeSku(rawSku) ?? throw new DemoProblem(400, "O SKU informado é inválido.");
        var fingerprint = Fingerprint("retry", new { id, sku });
        return transactions.ChangeAsync(sessionId, state =>
        {
            if (ReadPrevious(state, key, fingerprint) is { } previous)
            {
                return (previous, false);
            }

            var batch = FindBatch(state, id);
            if (!batch.Items.TryGetValue(sku, out var item))
            {
                throw new DemoProblem(404, "Item não encontrado neste lote.");
            }
            if (item.Status != ItemStatus.Failed)
            {
                throw new DemoProblem(409, "Somente itens com falha podem ser reprocessados no mesmo lote.");
            }
            if (!SyncWorker.HasCurrentSource(state, item))
            {
                throw new DemoProblem(409, "O estoque ou o vínculo mudou. Revise a divergência e crie um novo lote.");
            }

            StoreOperation(state, key, fingerprint, id);
            item.SendCount = 0;
            item.DueAt = transactions.Now;
            item.Record(transactions.Now, ItemStatus.Pending, "Reprocessamento solicitado.");
            return (new BatchReceipt(id), true);
        }, ct);
    }

    public async Task<BatchView[]> ReadBatchesAsync(string sessionId, CancellationToken ct)
    {
        var state = await transactions.RequireAsync(sessionId, ct);
        return state.Batches.Values.OrderByDescending(batch => batch.CreatedAt).ThenByDescending(batch => batch.Id)
            .Select(ToView).ToArray();
    }

    public async Task<BatchView> ReadBatchAsync(string sessionId, Guid id, CancellationToken ct) =>
        ToView(FindBatch(await transactions.RequireAsync(sessionId, ct), id));

    public static bool IsSessionId(string? id) => id is { Length: 64 } &&
        id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static SyncBatch FindBatch(SessionState state, Guid id) =>
        state.Batches.GetValueOrDefault(id) ?? throw new DemoProblem(404, "Lote não encontrado nesta sessão.");

    private static BatchView ToView(SyncBatch batch) => new(batch.Id, batch.CreatedAt, batch.Items.Values
        .OrderBy(item => item.Sku, StringComparer.Ordinal)
        .Select(item => new ItemView(item.Sku, item.ListingId, item.DesiredQuantity, item.Status, item.Message, [.. item.Attempts]))
        .ToArray());

    private static string Fingerprint<T>(string operation, T body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation + ":" + JsonSerializer.Serialize(body))));

    private static BatchReceipt? ReadPrevious(SessionState state, string key, string fingerprint)
    {
        if (!state.Operations.TryGetValue(key, out var previous))
        {
            return null;
        }
        if (previous.Fingerprint != fingerprint)
        {
            throw new DemoProblem(409, "A chave de idempotência já foi usada com outra operação.");
        }
        return new BatchReceipt(previous.BatchId);
    }

    private static void StoreOperation(SessionState state, string key, string fingerprint, Guid batchId)
    {
        if (state.Operations.Count >= MaxOperations)
        {
            throw new DemoProblem(422, "A sessão atingiu o limite de operações. Reinicie o cenário para continuar.");
        }

        state.Operations.Add(key, new IdempotencyEntry(fingerprint, batchId));
    }

    private static void SeedListings(SessionState state, DateTimeOffset now, bool mixed)
    {
        var seeds = new[]
        {
            new { Id = "LIST-001", Sku = "DEMO-001", Name = "Kit de canetas", Physical = 10, Reserved = 3, Remote = 12, Fault = SimulationFault.None },
            new { Id = "LIST-002", Sku = "DEMO-002", Name = "Caderno pontilhado", Physical = 6, Reserved = 2, Remote = 8, Fault = SimulationFault.LoseResponseAfterApply },
            new { Id = "LIST-003", Sku = "DEMO-003", Name = "Estojo de lona", Physical = 3, Reserved = 1, Remote = 6, Fault = SimulationFault.ChangeQuantityBeforeApply }
        };

        foreach (var seed in seeds)
        {
            state.Listings.Add(seed.Id, new Listing
            {
                Id = seed.Id,
                Name = seed.Name,
                Sku = mixed || seed.Id == "LIST-001" ? seed.Sku : null,
                Quantity = seed.Remote,
                ObservedAt = now,
                Fault = mixed ? seed.Fault : SimulationFault.None
            });

            if (mixed)
            {
                state.Stocks.Add(seed.Sku, new Stock(seed.Sku, seed.Physical, seed.Reserved, now, ++state.NextRevision));
            }
        }
    }
}
