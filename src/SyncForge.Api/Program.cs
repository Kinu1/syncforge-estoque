using System.Threading.RateLimiting;
using Amazon.Lambda.AspNetCoreServer.Hosting;
using SyncForge.Api;
using SyncForge.Core;

var builder = WebApplication.CreateBuilder(args);
var lambda = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_LAMBDA_RUNTIME_API"));
var settings = StorageSettings.FromEnvironment();
if (lambda && settings.Table is null)
{
    throw new InvalidOperationException("Defina SYNCFORGE_TABLE para executar no Lambda.");
}

if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null &&
    Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") is null &&
    builder.Configuration["urls"] is null)
{
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
}
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256 * 1024);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => settings.OpenStore());
builder.Services.AddSingleton<IWorkerWake>(services =>
    settings.OpenWake(services.GetRequiredService<ILoggerFactory>().CreateLogger("SyncForge.Queue")));
builder.Services.AddSingleton<SessionTransactions>();
builder.Services.AddSingleton<IMarketplace, SimulatedMarketplace>();
builder.Services.AddSingleton<DemoService>();
builder.Services.AddSingleton<SyncWorker>();
if (!lambda)
{
    builder.Services.AddHostedService<LocalWorker>();
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1"))
        {
            return RateLimitPartition.GetNoLimiter("static");
        }

        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var starting = context.Request.Path == "/api/v1/demo/session" &&
            DemoEndpoints.ReadCookie(context) is null;
        var scope = starting ? "start:" : "api:";
        return RateLimitPartition.GetFixedWindowLimiter(scope + address, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = starting ? 30 : 300,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

var app = builder.Build();
app.UseExceptionHandler(errors => errors.Run(context => Results.Problem(
    statusCode: 500,
    detail: "O SyncForge não concluiu a solicitação. Tente novamente.").ExecuteAsync(context)));
app.UseStatusCodePages(context =>
{
    var http = context.HttpContext;
    var status = http.Response.StatusCode;
    var detail = status switch
    {
        400 => "A solicitação está incompleta ou tem formato inválido.",
        413 => "O corpo da solicitação excede o limite aceito.",
        415 => "Envie o corpo da requisição como JSON.",
        429 => "Limite de solicitações atingido. Tente novamente em um minuto.",
        _ => "A solicitação não pôde ser atendida."
    };
    return Results.Problem(statusCode: status, detail: detail).ExecuteAsync(http);
});
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "private, no-store";
    }
    await next(context);
});
app.UseRateLimiter();

if (app.Services.GetRequiredService<ISessionStore>() is DynamoSessionStore dynamo)
{
    await dynamo.InitializeLocalAsync(app.Lifetime.ApplicationStopping);
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapOpenApi("/api/openapi/{documentName}.json");
app.MapDemoApi(lambda);
app.MapFallback("/api/{**rest}", () => Results.Problem(statusCode: 404, detail: "Rota da API não encontrada."));

if (app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}
await app.RunAsync();

public partial class Program;
