namespace SyncForge.Core;

public sealed record ImportRequest(string? Csv, DateTimeOffset? CapturedAt);
public sealed record ImportRow(int Line, string Sku, int PhysicalQuantity, int ReservedQuantity)
{
    public int AvailableQuantity => PhysicalQuantity - ReservedQuantity;
}
public sealed record ImportError(int Line, string Message);
public sealed record ImportPreview(Guid Id, ImportRow[] Rows, ImportError[] Errors);
public sealed record SessionResponse(DateTimeOffset ExpiresAt);
public sealed record ScenarioRequest(string? Scenario);
public sealed record LinkRequest(string? ListingId);
public sealed record SyncSelection(string? Sku, long Revision, long? ObservedVersion);
public sealed record SyncBatchRequest(SyncSelection[]? Items);
public sealed record BatchReceipt(Guid Id);
public sealed record DivergenceView(
    string Sku, int PhysicalQuantity, int ReservedQuantity, int AvailableQuantity,
    string? ListingId, int? ObservedQuantity, long? ObservedVersion,
    DateTimeOffset? ObservedAt, long Revision, string Status);
public sealed record ListingLinkView(string ListingId, string Name, string? Sku, int Quantity);
public sealed record BatchView(Guid Id, DateTimeOffset CreatedAt, ItemView[] Items);
public sealed record ItemView(string Sku, string ListingId, int DesiredQuantity, string Status, string Message, Attempt[] Attempts);
public sealed record Attempt(DateTimeOffset At, string Status, string Message);

public static class ItemStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string AwaitingConfirmation = "awaitingConfirmation";
    public const string Synchronized = "synchronized";
    public const string Conflict = "conflict";
    public const string Superseded = "superseded";
    public const string Failed = "failed";
}

public sealed record Stock(
    string Sku, int PhysicalQuantity, int ReservedQuantity, DateTimeOffset CapturedAt, long Revision)
{
    public int AvailableQuantity => PhysicalQuantity - ReservedQuantity;
}

public sealed class Listing
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Sku { get; set; }
    public int Quantity { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset ObservedAt { get; set; }
    public SimulationFault Fault { get; set; }
}

public enum SimulationFault
{
    None,
    LoseResponseAfterApply,
    ChangeQuantityBeforeApply,
    TransientFailure
}

public sealed record Import(ImportPreview Preview, DateTimeOffset CapturedAt, bool Confirmed = false);
public sealed record SyncBatch(Guid Id, DateTimeOffset CreatedAt, Dictionary<string, SyncItem> Items);
public sealed record IdempotencyEntry(string Fingerprint, Guid BatchId);

public sealed class SyncItem
{
    public required string Sku { get; init; }
    public required string ListingId { get; init; }
    public required int DesiredQuantity { get; init; }
    public required long Revision { get; init; }
    public required long ExpectedVersion { get; init; }
    public required Guid OperationId { get; init; }
    public string Status { get; set; } = ItemStatus.Pending;
    public string Message { get; set; } = "Aguardando processamento.";
    public DateTimeOffset? DueAt { get; set; }
    public Guid? LeaseToken { get; set; }
    public int SendCount { get; set; }
    public List<Attempt> Attempts { get; init; } = [];

    public void Record(DateTimeOffset at, string status, string message)
    {
        Status = status;
        Message = message;
        Attempts.Add(new Attempt(at, status, message));
        if (Attempts.Count > 20)
        {
            Attempts.RemoveAt(0);
        }
    }
}

public enum MarketplaceOutcome
{
    Applied,
    VersionConflict,
    TransientFailure,
    Unknown
}

public sealed class SessionState
{
    public required string Id { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public long StoreVersion { get; set; }
    public long NextRevision { get; set; }
    public Dictionary<string, Stock> Stocks { get; init; } = [];
    public Dictionary<string, Listing> Listings { get; init; } = [];
    public Dictionary<Guid, Import> Imports { get; init; } = [];
    public Dictionary<Guid, SyncBatch> Batches { get; init; } = [];
    public Dictionary<string, IdempotencyEntry> Operations { get; init; } = [];
    public Dictionary<Guid, MarketplaceOutcome> MarketplaceReceipts { get; init; } = [];

    public DateTimeOffset? NextDueAt() =>
        Batches.Values.SelectMany(batch => batch.Items.Values).Min(item => item.DueAt);
}

public sealed class DemoProblem(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
