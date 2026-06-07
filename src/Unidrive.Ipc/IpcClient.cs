using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Unidrive.Ipc;

/// <summary>
/// Pooled NDJSON client for the unidrive per-profile daemon. The wire is single-flight per
/// connection, so concurrency is a small pool of request/reply connections (kept under the
/// daemon's <c>MAX_CLIENTS = 10</c>); <see cref="SubscribeAsync"/> uses its own dedicated connection.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    // Leave headroom under the daemon's MAX_CLIENTS = 10 (pool + a subscribe connection + slack).
    private const int MaxPooledConnections = 6;

    private readonly string _socketPath;
    private readonly SemaphoreSlim _gate = new(MaxPooledConnections, MaxPooledConnections);
    private readonly ConcurrentBag<IpcConnection> _idle = new();

    public IpcClient(string profile) => _socketPath = IpcSocketPath.Resolve(profile);

    /// <summary>Test/diagnostic ctor: connect to an explicit socket path.</summary>
    public static IpcClient ForSocket(string socketPath) => new(socketPath, useRawPath: true);

    private IpcClient(string socketPath, bool useRawPath) => _socketPath = socketPath;

    public string SocketPath => _socketPath;

    /// <summary>
    /// Send <c>{"verb": verb, ...payload}</c> and return the parsed reply object. Throws
    /// <see cref="IpcException"/> on <c>{"ok":false}</c>; rethrows transport failures (the
    /// offending connection is dropped, not reused).
    /// </summary>
    public async Task<JsonElement> RequestAsync(
        string verb,
        IReadOnlyDictionary<string, object?>? payload = null,
        CancellationToken ct = default)
    {
        var requestJson = BuildRequest(verb, payload);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = _idle.TryTake(out var pooled)
                ? pooled
                : await IpcConnection.ConnectAsync(_socketPath, ct).ConfigureAwait(false);

            JsonElement root;
            try
            {
                using var reply = await conn.RequestAsync(requestJson, ct).ConfigureAwait(false);
                root = reply.RootElement.Clone();
            }
            catch
            {
                await conn.DisposeAsync().ConfigureAwait(false); // transport broke -> do not reuse
                throw;
            }

            _idle.Add(conn); // a parsed reply means the connection is healthy -> reuse

            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                throw new IpcException(verb, root.TryGetProperty("error", out var e) ? e.GetString() : null);

            return root;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The Phase 0.3 smoke verb: round-trip <c>daemon.status</c>.</summary>
    public async Task<DaemonStatus> DaemonStatusAsync(CancellationToken ct = default)
    {
        var r = await RequestAsync("daemon.status", null, ct).ConfigureAwait(false);
        return new DaemonStatus(
            UptimeMs: r.TryGetProperty("uptime_ms", out var u) ? u.GetInt64() : 0,
            ClientsConnected: r.TryGetProperty("clients_connected", out var c) ? c.GetInt32() : 0,
            RefreshInFlight: r.TryGetProperty("refresh_in_flight", out var rif) && rif.GetBoolean(),
            RefreshJobId: r.TryGetProperty("refresh_job_id", out var j) && j.ValueKind == JsonValueKind.String
                ? j.GetString()
                : null);
    }

    /// <summary>
    /// Open a file for reading via <c>hydration.open_read</c>. Triggers the daemon to download or
    /// locate the file in cache. Returns the cache path on success, null on not-found.
    /// </summary>
    public async Task<string?> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?> { ["handle"] = $"cli-{Guid.NewGuid():N}", ["path"] = path };
        var r = await RequestAsync("hydration.open_read", payload, ct).ConfigureAwait(false);
        return r.TryGetProperty("cache_path", out var cp) && cp.ValueKind == JsonValueKind.String
            ? cp.GetString()
            : null;
    }

    /// <summary>Close a handle opened with <see cref="OpenReadAsync"/>.</summary>
    public async Task CloseHandleAsync(string path, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?> { ["path"] = path };
        await RequestAsync("hydration.close_handle", payload, ct).ConfigureAwait(false);
    }

    /// <summary>List directory contents via <c>hydration.list</c>. Returns file entries.</summary>
    public async Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(
        string prefix = "", CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?> { ["prefix"] = prefix };
        var r = await RequestAsync("hydration.list", payload, ct).ConfigureAwait(false);

        var entries = new List<FileEntry>();
        if (r.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                entries.Add(new FileEntry(
                    Name: item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Size: item.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                    LastWriteTimeMs: item.TryGetProperty("mtime_ms", out var mt) ? mt.GetInt64() : 0,
                    IsDirectory: item.TryGetProperty("dir", out var d) && d.GetBoolean(),
                    IsHydrated: item.TryGetProperty("hydrated", out var h) && h.GetBoolean()));
            }
        }
        return entries;
    }

    /// <summary>
    /// Open a dedicated connection, issue <c>hydration.subscribe</c>, then stream the daemon's
    /// one-way event objects (hydrating / hydrated / view.invalidated / ...). Phase 1+ consumers
    /// drive CfAPI placeholder invalidation from these.
    /// </summary>
    public async IAsyncEnumerable<JsonElement> SubscribeAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var conn = await IpcConnection.ConnectAsync(_socketPath, ct).ConfigureAwait(false);
        await using (conn)
        {
            using (await conn.RequestAsync(BuildRequest("hydration.subscribe", null), ct).ConfigureAwait(false))
            {
                // ack is {"ok":true}; from here the daemon pushes events.
            }

            await foreach (var doc in conn.ReadEventsAsync(ct).ConfigureAwait(false))
            {
                using (doc) yield return doc.RootElement.Clone();
            }
        }
    }

    private static string BuildRequest(string verb, IReadOnlyDictionary<string, object?>? payload)
    {
        var obj = new JsonObject { ["verb"] = verb };
        if (payload is not null)
            foreach (var kv in payload)
                obj[kv.Key] = kv.Value is null ? null : JsonSerializer.SerializeToNode(kv.Value);
        return obj.ToJsonString();
    }

    public async ValueTask DisposeAsync()
    {
        while (_idle.TryTake(out var c)) await c.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
