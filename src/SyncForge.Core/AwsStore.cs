using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.SQS;
using SendMessageRequest = Amazon.SQS.Model.SendMessageRequest;
using Microsoft.Extensions.Logging;

namespace SyncForge.Core;

public sealed record StorageSettings(string? Table, string DueIndex, string? Endpoint, string? QueueUrl)
{
    public static StorageSettings FromEnvironment()
    {
        var endpoint = Environment.GetEnvironmentVariable("SYNCFORGE_DYNAMODB_ENDPOINT");
        var table = Environment.GetEnvironmentVariable("SYNCFORGE_TABLE");
        return new StorageSettings(
            table ?? (endpoint is not null ? "syncforge" : null),
            Environment.GetEnvironmentVariable("SYNCFORGE_DUE_INDEX") ?? "due-index",
            endpoint,
            Environment.GetEnvironmentVariable("SYNCFORGE_QUEUE_URL"));
    }

    public ISessionStore OpenStore() => Table is not null ? new DynamoSessionStore(this) : new MemorySessionStore();

    public IWorkerWake OpenWake(ILogger logger) =>
        QueueUrl is not null ? new SqsWorkerWake(new AmazonSQSClient(), QueueUrl, logger) : new NoWorkerWake();
}

public interface IWorkerWake
{
    Task NotifyAsync(string sessionId, TimeSpan delay, CancellationToken ct);
}

public sealed class NoWorkerWake : IWorkerWake
{
    public Task NotifyAsync(string sessionId, TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
}

public sealed record WorkerWakeMessage(string SessionId);

public sealed class SqsWorkerWake(IAmazonSQS sqs, string queueUrl, ILogger logger) : IWorkerWake, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task NotifyAsync(string sessionId, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                DelaySeconds = (int)Math.Clamp(Math.Ceiling(delay.TotalSeconds), 0, 900),
                MessageBody = JsonSerializer.Serialize(new WorkerWakeMessage(sessionId), Json)
            }, ct);
        }
        catch (AmazonServiceException exception)
        {
            logger.LogWarning(exception, "O aviso da fila falhou. O trabalho continua registrado para recuperação.");
        }
    }

    public void Dispose() => sqs.Dispose();
}

public sealed class DynamoSessionStore : ISessionStore, IDisposable
{
    private const string DuePartition = "due";
    private const string StateKey = "state";
    private const string SessionPrefix = "session#";

    private readonly StorageSettings _settings;
    private readonly IAmazonDynamoDB _db;

    public DynamoSessionStore(StorageSettings settings)
    {
        _settings = settings;
        _db = settings.Endpoint is null
            ? new AmazonDynamoDBClient()
            : new AmazonDynamoDBClient(new BasicAWSCredentials("syncforgelocal", "syncforgelocal"), new AmazonDynamoDBConfig
            {
                ServiceURL = settings.Endpoint,
                AuthenticationRegion = "us-east-1"
            });
    }

    private string Table => _settings.Table!;

    public async Task InitializeLocalAsync(CancellationToken ct)
    {
        if (_settings.Endpoint is null)
        {
            return;
        }

        try
        {
            await _db.DescribeTableAsync(Table, ct);
            return;
        }
        catch (ResourceNotFoundException)
        {
            // DynamoDB Local has no CloudFormation stack.
        }

        try
        {
            await _db.CreateTableAsync(new CreateTableRequest
            {
                TableName = Table,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                AttributeDefinitions =
                [
                    new("pk", ScalarAttributeType.S), new("sk", ScalarAttributeType.S),
                    new("duePk", ScalarAttributeType.S), new("dueSk", ScalarAttributeType.S)
                ],
                KeySchema = [new("pk", KeyType.HASH), new("sk", KeyType.RANGE)],
                GlobalSecondaryIndexes =
                [
                    new GlobalSecondaryIndex
                    {
                        IndexName = _settings.DueIndex,
                        KeySchema = [new("duePk", KeyType.HASH), new("dueSk", KeyType.RANGE)],
                        Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY }
                    }
                ]
            }, ct);
        }
        catch (ResourceInUseException)
        {
            // Another local process started the same table.
        }
    }

    public async Task<SessionState?> ReadAsync(string id, CancellationToken ct)
    {
        var result = await _db.GetItemAsync(new GetItemRequest
        {
            TableName = Table,
            ConsistentRead = true,
            Key = SessionKey(id)
        }, ct);

        if (result.Item is null || !result.Item.TryGetValue("payload", out var payload))
        {
            return null;
        }
        using var decoded = new GZipStream(payload.B, CompressionMode.Decompress);
        return await JsonSerializer.DeserializeAsync<SessionState>(decoded, cancellationToken: ct);
    }

    public async Task<bool> TrySaveAsync(SessionState state, long? expected, CancellationToken ct)
    {
        state.StoreVersion = (expected ?? 0) + 1;
        using var encoded = new MemoryStream();
        await using (var compression = new GZipStream(encoded, CompressionLevel.Fastest, leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(compression, state, cancellationToken: ct);
        }
        if (encoded.Length > 300 * 1024)
        {
            throw new DemoProblem(422, "A demonstração atingiu seu limite de dados. Reinicie o cenário para continuar.");
        }
        encoded.Position = 0;

        var item = SessionKey(state.Id);
        item["payload"] = new AttributeValue { B = encoded };
        item["revision"] = Number(state.StoreVersion);
        item["expiresAt"] = Number(state.ExpiresAt.ToUnixTimeSeconds());
        if (state.NextDueAt() is { } due && due < state.ExpiresAt)
        {
            item["duePk"] = String(DuePartition);
            item["dueSk"] = String(DueKey(due));
        }

        var request = new PutItemRequest { TableName = Table, Item = item };
        if (expected is null)
        {
            request.ConditionExpression = "attribute_not_exists(pk)";
        }
        else
        {
            request.ConditionExpression = "#revision = :expected";
            request.ExpressionAttributeNames = new() { ["#revision"] = "revision" };
            request.ExpressionAttributeValues = new() { [":expected"] = Number(expected.Value) };
        }

        try
        {
            await _db.PutItemAsync(request, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> FindDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        const int limit = 50;
        var due = new List<string>(limit);
        var nowInSeconds = now.ToUnixTimeSeconds();
        var request = new QueryRequest
        {
            TableName = Table,
            IndexName = _settings.DueIndex,
            Limit = limit,
            KeyConditionExpression = "duePk = :partition and dueSk <= :now",
            ProjectionExpression = "pk",
            ExpressionAttributeValues = new()
            {
                [":partition"] = String(DuePartition),
                [":now"] = String(DueKey(now))
            }
        };

        do
        {
            var page = await _db.QueryAsync(request, ct);
            foreach (var item in page.Items ?? [])
            {
                var sessionId = item["pk"].S[SessionPrefix.Length..];

                // TTL removes expired sessions asynchronously. Read only their expiration
                // so stale index entries cannot occupy all available reconciliation slots.
                var session = await _db.GetItemAsync(new GetItemRequest
                {
                    TableName = Table,
                    Key = SessionKey(sessionId),
                    ProjectionExpression = "expiresAt"
                }, ct);
                if (session.Item is null || !session.Item.TryGetValue("expiresAt", out var expiry) ||
                    !long.TryParse(expiry.N, CultureInfo.InvariantCulture, out var expiresAt) || expiresAt <= nowInSeconds)
                {
                    continue;
                }

                due.Add(sessionId);
                if (due.Count == limit)
                {
                    return due;
                }
            }
            request.ExclusiveStartKey = page.LastEvaluatedKey;
        } while (request.ExclusiveStartKey is { Count: > 0 });

        return due;
    }

    private static string DueKey(DateTimeOffset time) =>
        time.ToUnixTimeMilliseconds().ToString("D16", CultureInfo.InvariantCulture);

    private static Dictionary<string, AttributeValue> SessionKey(string id) =>
        new() { ["pk"] = String(SessionPrefix + id), ["sk"] = String(StateKey) };

    private static AttributeValue String(string value) => new() { S = value };
    private static AttributeValue Number(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    public void Dispose() => _db.Dispose();
}
