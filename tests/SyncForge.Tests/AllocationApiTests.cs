using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SyncForge.Core;
using Xunit;

namespace SyncForge.Tests;

public sealed class AllocationApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Root = "/api/v1";
    private const string Allocations = Root + "/allocations";

    [Fact]
    public async Task ExistingSessionCanImportAndBindBothChannelsWithoutReset()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var oldPayload = JsonSerializer.Serialize(new
        {
            Id = id,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Listings = new Dictionary<string, object>
            {
                ["LEGACY-A"] = new { Id = "LEGACY-A", Name = "Produto antigo", Quantity = 5, Version = 4 }
            }
        });
        var oldSession = JsonSerializer.Deserialize<SessionState>(oldPayload)!;
        Assert.True(await factory.Services.GetRequiredService<ISessionStore>().TrySaveAsync(oldSession, null, ct));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"syncforge_session={id}");

        var imported = await client.PostAsJsonAsync(Root + "/imports", new ImportRequest(
            "sku,physical_quantity,reserved_quantity\nNOVO,12,5", DateTimeOffset.UtcNow), ct);
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var preview = (await imported.Content.ReadFromJsonAsync<ImportPreview>(ct))!;
        (await client.PostAsync($"{Root}/imports/{preview.Id}/confirm", null, ct)).EnsureSuccessStatusCode();

        var view = (await client.GetFromJsonAsync<AllocationView>(Allocations, ct))!;
        var stock = Assert.Single(view.Stocks);
        var a = Assert.Single(view.Listings, l => l.Channel == "A");
        Assert.Equal("LEGACY-A", a.ListingId);
        var b = view.Listings.First(l => l.Channel == "B" && l.Sku is null);
        Assert.Equal(7, stock.AvailableQuantity);
        Assert.Null(stock.PlannedA);
        var request = new AllocationRequest(stock.Revision, new(a.ListingId, a.Version, 3), new(b.ListingId, b.Version, 2));
        using var result = await PostWithKey(client, Allocations + "/NOVO", request, "vincular", ct);
        Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);

        view = (await client.GetFromJsonAsync<AllocationView>(Allocations, ct))!;
        stock = Assert.Single(view.Stocks);
        Assert.Equal(3, stock.PlannedA);
        Assert.Equal(2, stock.PlannedB);
        Assert.Equal(2, stock.AvailableQuantity - stock.PlannedA - stock.PlannedB);
        Assert.Equal(new[] { a.ListingId, b.ListingId }, view.Listings.Where(l => l.Sku == stock.Sku).OrderBy(l => l.Channel).Select(l => l.ListingId));
    }

    [Fact]
    public async Task ReplayReturnsSameBatchAndKeepsOtherSessionsAndLegacyWritesIsolated()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();
        using var other = factory.CreateClient();
        var view = await LoadScenario(client, ct);
        var sku = Assert.Single(view.Stocks).Sku;
        var path = $"{Allocations}/{sku}";
        var request = BuildRequest(view);
        using var result = await PostWithKey(client, path, request, "dividir", ct);
        Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);
        var receipt = (await result.Content.ReadFromJsonAsync<BatchReceipt>(ct))!;
        var location = $"{Root}/sync-batches/{receipt.Id}";
        Assert.Equal(location, result.Headers.Location?.OriginalString);
        using var replay = await PostWithKey(client, path, request, "dividir", ct);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(receipt, await replay.Content.ReadFromJsonAsync<BatchReceipt>(ct));
        Assert.Single((await client.GetFromJsonAsync<BatchView[]>(Root + "/sync-batches", ct))!);
        using var changed = await PostWithKey(client, path,
            request with { ChannelB = request.ChannelB! with { Quantity = 4 } }, "dividir", ct);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);

        view = (await client.GetFromJsonAsync<AllocationView>(Allocations, ct))!;
        var stock = Assert.Single(view.Stocks);
        var listing = Assert.Single(view.Listings, l => l.Sku == sku && l.Channel == "A");
        var legacyBatch = new SyncBatchRequest([new(sku, stock.Revision, listing.Version)]);
        using var bypass = await PostWithKey(client, Root + "/sync-batches", legacyBatch, "legado", ct);
        Assert.Equal(HttpStatusCode.Conflict, bypass.StatusCode);
        using var relink = await client.PutAsJsonAsync($"{Root}/listing-links/{sku}", new LinkRequest(listing.ListingId), ct);
        Assert.Equal(HttpStatusCode.Conflict, relink.StatusCode);
        using var retry = await PostWithKey(client, $"{location}/items/{sku}/retry", new { }, "retry-legado", ct);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<DivergenceView[]>(Root + "/divergences", ct))!);

        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync(Allocations, ct)).StatusCode);
        (await other.PostAsync(Root + "/demo/session", null, ct)).EnsureSuccessStatusCode();
        Assert.Empty((await other.GetFromJsonAsync<AllocationView>(Allocations, ct))!.Stocks);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(location, ct)).StatusCode);
        using var foreign = await PostWithKey(other, path, request, "dividir", ct);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task InvalidAndStaleRequestsLeavePlanAndBatchHistoryIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();
        var view = await LoadScenario(client, ct);
        var stock = Assert.Single(view.Stocks);
        var path = $"{Allocations}/{stock.Sku}";
        var request = BuildRequest(view);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, request, ct)).StatusCode);
        var a = request.ChannelA!;
        var missing = new { request.Revision, channelA = new { a.ListingId, a.Version }, request.ChannelB };
        using var absentQuantity = await PostWithKey(client, path, missing, "incompleto", ct);
        Assert.Equal(HttpStatusCode.BadRequest, absentQuantity.StatusCode);

        (AllocationRequest Body, HttpStatusCode Expected)[] cases =
        [
            (request with { ChannelB = null }, HttpStatusCode.BadRequest),
            (request with { ChannelA = a with { Quantity = -1 } }, HttpStatusCode.BadRequest),
            (request with { ChannelA = a with { ListingId = request.ChannelB!.ListingId } }, HttpStatusCode.BadRequest),
            (request with { ChannelA = a with { Quantity = int.MaxValue } }, HttpStatusCode.UnprocessableEntity),
            (request with { ChannelA = a with { Version = a.Version + 1 } }, HttpStatusCode.Conflict),
            (request with { Revision = request.Revision + 1 }, HttpStatusCode.Conflict)
        ];
        foreach (var (body, expected) in cases)
        {
            using var rejected = await PostWithKey(client, path, body, "corrigir", ct);
            Assert.Equal(expected, rejected.StatusCode);
        }
        Assert.Equal(stock, Assert.Single((await client.GetFromJsonAsync<AllocationView>(Allocations, ct))!.Stocks));
        Assert.Empty((await client.GetFromJsonAsync<BatchView[]>(Root + "/sync-batches", ct))!);
    }

    private static async Task<AllocationView> LoadScenario(HttpClient client, CancellationToken ct)
    {
        (await client.PostAsync(Root + "/demo/session", null, ct)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(Root + "/demo/scenario", new ScenarioRequest("transfer"), ct)).EnsureSuccessStatusCode();
        return (await client.GetFromJsonAsync<AllocationView>(Allocations, ct))!;
    }

    private static AllocationRequest BuildRequest(AllocationView view)
    {
        var stock = Assert.Single(view.Stocks);
        var a = Assert.Single(view.Listings, l => l.Sku == stock.Sku && l.Channel == "A");
        var b = Assert.Single(view.Listings, l => l.Sku == stock.Sku && l.Channel == "B");
        return new(stock.Revision, new(a.ListingId, a.Version, 2), new(b.ListingId, b.Version, 5));
    }

    private static async Task<HttpResponseMessage> PostWithKey<T>(HttpClient client, string path, T body, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, ct);
    }
}
