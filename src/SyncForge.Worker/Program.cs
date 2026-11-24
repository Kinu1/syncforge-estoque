using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Microsoft.Extensions.Logging;
using SyncForge.Core;

var settings = StorageSettings.FromEnvironment();
if (settings.Table is null)
{
    throw new InvalidOperationException("Defina SYNCFORGE_TABLE para executar o worker.");
}

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole());
using var store = new DynamoSessionStore(settings);
var wake = settings.OpenWake(loggerFactory.CreateLogger("SyncForge.Queue"));
using var wakeLifetime = wake as IDisposable;
var transactions = new SessionTransactions(store, TimeProvider.System);
var worker = new SyncWorker(
    store, transactions, new SimulatedMarketplace(transactions), wake, loggerFactory.CreateLogger<SyncWorker>());

await LambdaBootstrapBuilder.Create<JsonElement>(ExecuteAsync, new DefaultLambdaJsonSerializer()).Build().RunAsync();

async Task ExecuteAsync(JsonElement input, ILambdaContext context)
{
    var budget = context.RemainingTime - TimeSpan.FromSeconds(3);
    using var deadline = new CancellationTokenSource(budget > TimeSpan.Zero ? budget : TimeSpan.Zero);
    var ct = deadline.Token;

    if (input.TryGetProperty("Records", out var records))
    {
        foreach (var record in records.EnumerateArray())
        {
            using var message = JsonDocument.Parse(record.GetProperty("body").GetString()!);
            if (!message.RootElement.TryGetProperty("sessionId", out var value) ||
                !DemoService.IsSessionId(value.GetString()))
            {
                throw new InvalidDataException("A mensagem não contém uma sessão válida.");
            }
            await worker.RunSessionAsync(value.GetString()!, ct);
        }
        return;
    }

    await worker.RunDueAsync(ct);
}
