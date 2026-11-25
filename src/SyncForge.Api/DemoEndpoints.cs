using SyncForge.Core;

namespace SyncForge.Api;

public static class DemoEndpoints
{
    private const string CookieName = "syncforge_session";

    public static void MapDemoApi(this WebApplication app, bool lambda)
    {
        var frontendOrigin = Environment.GetEnvironmentVariable("SYNCFORGE_FRONTEND_ORIGIN");
        var routes = app.MapGroup("/api/v1").AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            if (HttpMethods.IsPost(http.Request.Method) || HttpMethods.IsPut(http.Request.Method))
            {
                if (!IsTrustedBrowserRequest(http.Request, frontendOrigin))
                {
                    return Results.Problem(statusCode: 403, detail: "Esta ação aceita somente a origem da demonstração.");
                }
            }
            try
            {
                return await next(invocation);
            }
            catch (DemoProblem problem)
            {
                return Results.Problem(statusCode: problem.Status, detail: problem.Message);
            }
        });

        routes.MapPost("/demo/session", async (HttpContext http, DemoService service, CancellationToken ct) =>
        {
            var state = await service.StartAsync(ReadCookie(http), ct);
            http.Response.Cookies.Append(CookieName, state.Id, new CookieOptions
            {
                HttpOnly = true,
                Secure = lambda || http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/api/v1",
                Expires = state.ExpiresAt,
                IsEssential = true
            });
            return Results.Ok(new SessionResponse(state.ExpiresAt));
        }).Produces<SessionResponse>();

        routes.MapPost("/demo/scenario", async (HttpContext http, ScenarioRequest request, DemoService service, CancellationToken ct) =>
        {
            await service.ResetAsync(RequireCookie(http), request.Scenario, ct);
            return Results.NoContent();
        }).Produces(204);

        routes.MapPost("/imports", async (HttpContext http, ImportRequest request, DemoService service, CancellationToken ct) =>
        {
            var preview = await service.PreviewAsync(RequireCookie(http), request, ct);
            return Results.Created($"/api/v1/imports/{preview.Id}/confirm", preview);
        }).Produces<ImportPreview>(201);

        routes.MapPost("/imports/{id:guid}/confirm", async (HttpContext http, Guid id, DemoService service, CancellationToken ct) =>
        {
            await service.ConfirmAsync(RequireCookie(http), id, ct);
            return Results.NoContent();
        }).Produces(204);

        routes.MapGet("/divergences", async (HttpContext http, DemoService service, CancellationToken ct) =>
            Results.Ok(await service.ReadDivergencesAsync(RequireCookie(http), ct)))
            .Produces<DivergenceView[]>();

        routes.MapGet("/allocations", async (HttpContext http, DemoService service, CancellationToken ct) =>
            Results.Ok(await service.ReadAllocationsAsync(RequireCookie(http), ct)))
            .Produces<AllocationView>();

        routes.MapPost("/allocations/{sku}", async (
            HttpContext http, string sku, AllocationRequest request, DemoService service, IWorkerWake wake, CancellationToken ct) =>
        {
            var sessionId = RequireCookie(http);
            var result = await service.AllocateAsync(sessionId, sku, request, ReadIdempotencyKey(http), ct);
            await wake.NotifyAsync(sessionId, TimeSpan.Zero, ct);
            return Results.Accepted($"/api/v1/sync-batches/{result.Id}", result);
        }).Produces<BatchReceipt>(202).ProducesProblem(400).ProducesProblem(409).ProducesProblem(422);

        routes.MapGet("/listing-links", async (HttpContext http, DemoService service, CancellationToken ct) =>
            Results.Ok(await service.ReadLinksAsync(RequireCookie(http), ct)))
            .Produces<ListingLinkView[]>();

        routes.MapPut("/listing-links/{sku}", async (HttpContext http, string sku, LinkRequest request, DemoService service, CancellationToken ct) =>
        {
            await service.LinkAsync(RequireCookie(http), sku, request.ListingId, ct);
            return Results.NoContent();
        }).Produces(204);

        routes.MapPost("/sync-batches", async (
            HttpContext http, SyncBatchRequest request, DemoService service, IWorkerWake wake, CancellationToken ct) =>
        {
            var sessionId = RequireCookie(http);
            var result = await service.CreateBatchAsync(sessionId, request.Items, ReadIdempotencyKey(http), ct);
            await wake.NotifyAsync(sessionId, TimeSpan.Zero, ct);
            return Results.Accepted($"/api/v1/sync-batches/{result.Id}", result);
        }).Produces<BatchReceipt>(202);

        routes.MapGet("/sync-batches", async (HttpContext http, DemoService service, CancellationToken ct) =>
            Results.Ok(await service.ReadBatchesAsync(RequireCookie(http), ct)))
            .Produces<BatchView[]>();

        routes.MapGet("/sync-batches/{id:guid}", async (HttpContext http, Guid id, DemoService service, CancellationToken ct) =>
            Results.Ok(await service.ReadBatchAsync(RequireCookie(http), id, ct)))
            .Produces<BatchView>();

        routes.MapPost("/sync-batches/{id:guid}/items/{sku}/retry", async (
            HttpContext http, Guid id, string sku, DemoService service, IWorkerWake wake, CancellationToken ct) =>
        {
            var sessionId = RequireCookie(http);
            var result = await service.RetryAsync(sessionId, id, sku, ReadIdempotencyKey(http), ct);
            await wake.NotifyAsync(sessionId, TimeSpan.Zero, ct);
            return Results.Accepted($"/api/v1/sync-batches/{result.Id}", result);
        }).Produces<BatchReceipt>(202);
    }

    public static string? ReadCookie(HttpContext http)
    {
        var value = http.Request.Cookies[CookieName];
        return DemoService.IsSessionId(value) ? value : null;
    }

    private static string RequireCookie(HttpContext http) =>
        ReadCookie(http) ?? throw new DemoProblem(401, "Inicie uma sessão de demonstração.");

    private static string ReadIdempotencyKey(HttpContext http)
    {
        var values = http.Request.Headers["Idempotency-Key"];
        var value = values.Count == 1 ? values[0] : null;
        if (value is not { Length: >= 1 and <= 128 } ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new DemoProblem(400, "Informe uma chave de idempotência válida no cabeçalho Idempotency-Key.");
        }

        return value;
    }

    private static bool IsTrustedBrowserRequest(HttpRequest request, string? frontendOrigin)
    {
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite is "cross-site" or "same-site")
        {
            return false;
        }

        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return true;
        }
        if (frontendOrigin is not null)
        {
            return string.Equals(origin, frontendOrigin, StringComparison.OrdinalIgnoreCase);
        }

        return fetchSite == "same-origin" ||
            string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }
}
