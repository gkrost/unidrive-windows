using System.Net.Sockets;
using System.Text;
using Unidrive.Ipc;
using Xunit;

namespace Unidrive.Ipc.Tests;

/// <summary>
/// End-to-end IPC round-trip against a byte-faithful mock of the engine's daemon. The mock emits the
/// exact bytes the real Kotlin handler does — see <c>DaemonRuntime.kt</c> (the <c>daemon.status</c>
/// handler):
/// <code>{"ok":true,"uptime_ms":$uptimeMs,"clients_connected":$clientCount,"refresh_in_flight":$refreshInFlight,"refresh_job_id":$jobIdJson}</code>
/// and the error envelope <c>{"ok":false,"error":"&lt;token&gt;"}</c>. This exercises the whole client
/// path — <see cref="IpcSocketPath.Resolve"/>, AF_UNIX connect, NDJSON framing, request build, reply
/// parse, and the <see cref="DaemonStatus"/>/<see cref="IpcException"/> mapping — which is the
/// Phase 0.3 acceptance. (A live-JVM smoke additionally needs a profile that authenticates.)
/// </summary>
public class DaemonStatusRoundTripTests
{
    private static string UniqueProfile() => "itest-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task RoundTrips_DaemonStatus_ParsesAllFields()
    {
        var sock = IpcSocketPath.Resolve(UniqueProfile());
        await using var daemon = MockDaemon.Start(
            sock,
            _ => """{"ok":true,"uptime_ms":12345,"clients_connected":2,"refresh_in_flight":false,"refresh_job_id":null}""");
        await using var client = IpcClient.ForSocket(sock);

        var status = await client.DaemonStatusAsync();

        Assert.Equal(12345L, status.UptimeMs);
        Assert.Equal(2, status.ClientsConnected);
        Assert.False(status.RefreshInFlight);
        Assert.Null(status.RefreshJobId);
        // The client framed the request exactly as the engine's DaemonCommand/DaemonRuntimeTest expect.
        Assert.Equal("""{"verb":"daemon.status"}""", await daemon.FirstRequest);
    }

    [Fact]
    public async Task RoundTrips_DaemonStatus_RefreshInFlight_WithJobId()
    {
        var sock = IpcSocketPath.Resolve(UniqueProfile());
        await using var daemon = MockDaemon.Start(
            sock,
            _ => """{"ok":true,"uptime_ms":42,"clients_connected":1,"refresh_in_flight":true,"refresh_job_id":"job-7"}""");
        await using var client = IpcClient.ForSocket(sock);

        var status = await client.DaemonStatusAsync();

        Assert.Equal(42L, status.UptimeMs);
        Assert.Equal(1, status.ClientsConnected);
        Assert.True(status.RefreshInFlight);
        Assert.Equal("job-7", status.RefreshJobId);
    }

    [Fact]
    public async Task ErrorReply_NotFound_ThrowsIpcException_IsNotFound()
    {
        var sock = IpcSocketPath.Resolve(UniqueProfile());
        await using var daemon = MockDaemon.Start(
            sock,
            _ => """{"ok":false,"error":"not_found"}""");
        await using var client = IpcClient.ForSocket(sock);

        var ex = await Assert.ThrowsAsync<IpcException>(
            () => client.RequestAsync("hydration.last_synced", new Dictionary<string, object?> { ["path"] = "/missing" }));

        Assert.True(ex.IsNotFound);
        Assert.Equal("hydration.last_synced", ex.Verb);
        Assert.Equal("not_found", ex.ErrorToken);
        // Payload serialisation: verb first, then the path key (insertion order).
        Assert.Equal("""{"verb":"hydration.last_synced","path":"/missing"}""", await daemon.FirstRequest);
    }

    /// <summary>
    /// Minimal AF_UNIX NDJSON server: binds the given socket path, and for every request line invokes
    /// <paramref name="responder"/> and writes its result back as one <c>\n</c>-terminated line — the
    /// same one-request-line → one-reply-line framing as <c>IpcServer</c>.
    /// </summary>
    private sealed class MockDaemon : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serveTask;
        private readonly TaskCompletionSource<string> _firstRequest =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string SocketPath { get; }

        /// <summary>The first request line the server received (race-free; set before its reply is sent).</summary>
        public Task<string> FirstRequest => _firstRequest.Task;

        private MockDaemon(string socketPath, Socket listener, Func<string, string> responder)
        {
            SocketPath = socketPath;
            _listener = listener;
            _serveTask = Task.Run(() => ServeAsync(responder, _cts.Token));
        }

        public static MockDaemon Start(string socketPath, Func<string, string> responder)
        {
            if (File.Exists(socketPath)) File.Delete(socketPath); // AF_UNIX bind needs a free path
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(8);
            return new MockDaemon(socketPath, listener, responder);
        }

        private async Task ServeAsync(Func<string, string> responder, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var conn = await _listener.AcceptAsync(ct).ConfigureAwait(false);
                    _ = Task.Run(() => HandleAsync(conn, responder, ct), ct);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (ObjectDisposedException) { /* listener disposed */ }
        }

        private async Task HandleAsync(Socket conn, Func<string, string> responder, CancellationToken ct)
        {
            using (conn)
            {
                var buffer = new byte[8192];
                var pending = new List<byte>();
                while (!ct.IsCancellationRequested)
                {
                    int nl = pending.IndexOf((byte)'\n');
                    if (nl < 0)
                    {
                        int n;
                        try { n = await conn.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false); }
                        catch { break; }
                        if (n == 0) break; // EOF
                        for (int i = 0; i < n; i++) pending.Add(buffer[i]);
                        continue;
                    }

                    int end = nl;
                    if (end > 0 && pending[end - 1] == (byte)'\r') end--;
                    var request = Encoding.UTF8.GetString(pending.GetRange(0, end).ToArray());
                    pending.RemoveRange(0, nl + 1);
                    _firstRequest.TrySetResult(request);

                    var reply = Encoding.UTF8.GetBytes(responder(request) + "\n");
                    await conn.SendAsync(reply, SocketFlags.None, ct).ConfigureAwait(false);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Dispose();
            try { await _serveTask.ConfigureAwait(false); } catch { /* best-effort */ }
            try { if (File.Exists(SocketPath)) File.Delete(SocketPath); } catch { /* best-effort */ }
            _cts.Dispose();
        }
    }
}
