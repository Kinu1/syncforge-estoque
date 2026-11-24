using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SyncForge.Core;
using Xunit;

namespace SyncForge.Tests;

public sealed class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task PublicRoutesAndSessionIsolation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var first = factory.CreateClient();
        using var second = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/health", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/v1/divergences", ct)).StatusCode);

        var opened = await first.PostAsync("/api/v1/demo/session", null, ct);
        opened.EnsureSuccessStatusCode();
        var cookie = Assert.Single(opened.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        (await second.PostAsync("/api/v1/demo/session", null, ct)).EnsureSuccessStatusCode();

        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/v1/demo/scenario")
        {
            Content = JsonContent.Create(new ScenarioRequest("mixed"))
        };
        foreign.Headers.Add("Origin", "https://example.com");
        foreign.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await first.SendAsync(foreign, ct)).StatusCode);

        var seeded = await first.PostAsJsonAsync("/api/v1/demo/scenario", new ScenarioRequest("mixed"), ct);
        Assert.Equal(HttpStatusCode.NoContent, seeded.StatusCode);
        var firstRows = await first.GetFromJsonAsync<DivergenceView[]>("/api/v1/divergences", ct);
        var secondRows = await second.GetFromJsonAsync<DivergenceView[]>("/api/v1/divergences", ct);
        Assert.All(Assert.IsType<DivergenceView[]>(firstRows), row => Assert.Equal("divergent", row.Status));
        Assert.Equal(3, firstRows!.Length);
        Assert.Empty(Assert.IsType<DivergenceView[]>(secondRows));
    }

    [Fact]
    public async Task ImportConfirmationAndBatchContract()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();
        (await client.PostAsync("/api/v1/demo/session", null, ct)).EnsureSuccessStatusCode();
        var imported = await client.PostAsJsonAsync("/api/v1/imports", new ImportRequest(
            "sku,physical_quantity,reserved_quantity\nDEMO-001,10,3\nMANUAL,0,0", DateTimeOffset.UtcNow), ct);
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var preview = Assert.IsType<ImportPreview>(await imported.Content.ReadFromJsonAsync<ImportPreview>(ct));
        Assert.Empty(preview.Errors);
        Assert.Equal(2, preview.Rows.Length);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(
            $"/api/v1/imports/{preview.Id}/confirm", null, ct)).StatusCode);

        var rows = Assert.IsType<DivergenceView[]>(
            await client.GetFromJsonAsync<DivergenceView[]>("/api/v1/divergences", ct));
        Assert.Equal("unlinked", rows[1].Status);
        var body = new SyncBatchRequest([new(rows[0].Sku, rows[0].Revision, rows[0].ObservedVersion)]);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/sync-batches", body, ct)).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sync-batches") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", "api-test");
        var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = Assert.IsType<BatchReceipt>(await response.Content.ReadFromJsonAsync<BatchReceipt>(ct));
        var batch = Assert.IsType<BatchView>(
            await client.GetFromJsonAsync<BatchView>($"/api/v1/sync-batches/{receipt.Id}", ct));
        Assert.Equal("DEMO-001", Assert.Single(batch.Items).Sku);
    }
}
