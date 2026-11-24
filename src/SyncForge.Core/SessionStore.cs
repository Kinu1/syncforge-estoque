using System.Collections.Concurrent;
using System.Text.Json;

namespace SyncForge.Core;

public interface ISessionStore
{
    Task<SessionState?> ReadAsync(string id, CancellationToken ct);
    Task<bool> TrySaveAsync(SessionState state, long? expected, CancellationToken ct);
    Task<IReadOnlyList<string>> FindDueAsync(DateTimeOffset now, CancellationToken ct);
}

public sealed class MemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, StoredSession> _sessions = new();
    private sealed record StoredSession(long Version, byte[] Payload, DateTimeOffset? DueAt, DateTimeOffset ExpiresAt);

    public Task<SessionState?> ReadAsync(string id, CancellationToken ct) => Task.FromResult(
        _sessions.TryGetValue(id, out var current) ? JsonSerializer.Deserialize<SessionState>(current.Payload) : null);

    public Task<bool> TrySaveAsync(SessionState state, long? expected, CancellationToken ct)
    {
        state.StoreVersion = (expected ?? 0) + 1;
        var next = new StoredSession(
            state.StoreVersion, JsonSerializer.SerializeToUtf8Bytes(state), state.NextDueAt(), state.ExpiresAt);
        if (expected is null)
        {
            return Task.FromResult(_sessions.TryAdd(state.Id, next));
        }

        var updated = _sessions.TryGetValue(state.Id, out var current) &&
            current.Version == expected && _sessions.TryUpdate(state.Id, next, current);
        return Task.FromResult(updated);
    }

    public Task<IReadOnlyList<string>> FindDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (id, session) in _sessions)
        {
            if (session.ExpiresAt <= now)
            {
                _sessions.TryRemove(new KeyValuePair<string, StoredSession>(id, session));
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(_sessions
            .Where(entry => entry.Value.DueAt <= now)
            .OrderBy(entry => entry.Value.DueAt)
            .Take(50)
            .Select(entry => entry.Key)
            .ToArray());
    }
}

public sealed class SessionTransactions(ISessionStore store, TimeProvider time)
{
    public DateTimeOffset Now => time.GetUtcNow();

    public async Task<SessionState> RequireAsync(string? id, CancellationToken ct)
    {
        var state = id is not null ? await store.ReadAsync(id, ct) : null;
        if (state is null || state.ExpiresAt <= Now)
        {
            throw new DemoProblem(401, "Inicie uma sessão de demonstração.");
        }
        return state;
    }

    public async Task<T> ChangeAsync<T>(
        string id, Func<SessionState, (T Value, bool Changed)> apply, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var state = await RequireAsync(id, ct);
            var version = state.StoreVersion;
            var result = apply(state);
            if (!result.Changed || await store.TrySaveAsync(state, version, ct))
            {
                return result.Value;
            }
            await Task.Delay(Random.Shared.Next(2, 12) * (attempt + 1), ct);
        }
        throw new DemoProblem(409, "A sessão recebeu alterações concorrentes. Atualize os dados e tente novamente.");
    }
}
