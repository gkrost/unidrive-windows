using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Unidrive.Ipc;

/// <summary>
/// One AF_UNIX, newline-delimited-JSON connection to the unidrive daemon. The wire is strictly
/// one request line -&gt; one reply line (no request IDs), so a connection is single-flight;
/// concurrency comes from the pool in <see cref="IpcClient"/>.
/// </summary>
internal sealed class IpcConnection : IAsyncDisposable
{
    private const int MaxRequestBytes = 64 * 1024; // mirrors IpcServer.MAX_REQUEST_BYTES
    private const byte Lf = 10; // '\n'
    private const byte Cr = 13; // '\r'

    private readonly Socket _socket;
    private readonly byte[] _readBuffer = new byte[8192];
    private readonly List<byte> _pending = new();

    private IpcConnection(Socket socket) => _socket = socket;

    public static async Task<IpcConnection> ConnectAsync(string socketPath, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
        return new IpcConnection(socket);
    }

    /// <summary>Send one request object as an NDJSON line and read exactly one reply line.</summary>
    public async Task<JsonDocument> RequestAsync(string requestJson, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(requestJson);
        if (body.Length + 1 > MaxRequestBytes)
            throw new InvalidOperationException($"IPC request exceeds {MaxRequestBytes} bytes");

        var line = new byte[body.Length + 1];
        Array.Copy(body, line, body.Length);
        line[body.Length] = Lf;
        await _socket.SendAsync(line, SocketFlags.None, ct).ConfigureAwait(false);

        var reply = await ReadLineAsync(ct).ConfigureAwait(false)
            ?? throw new IOException("daemon closed the connection before replying");
        return JsonDocument.Parse(reply);
    }

    /// <summary>Stream newline-delimited JSON events (used after a hydration.subscribe ack).</summary>
    public async IAsyncEnumerable<JsonDocument> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) yield break;       // connection closed
            if (line.Length == 0) continue;      // keepalive / blank
            yield return JsonDocument.Parse(line);
        }
    }

    private async Task<byte[]?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            int idx = _pending.IndexOf(Lf);
            if (idx >= 0)
            {
                int end = idx;
                if (end > 0 && _pending[end - 1] == Cr) end--; // strip a trailing CR
                var lineBytes = new byte[end];
                _pending.CopyTo(0, lineBytes, 0, end);
                _pending.RemoveRange(0, idx + 1);
                return lineBytes;
            }

            int n = await _socket.ReceiveAsync(_readBuffer, SocketFlags.None, ct).ConfigureAwait(false);
            if (n == 0) return null; // EOF
            for (int i = 0; i < n; i++) _pending.Add(_readBuffer[i]);
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _socket.Shutdown(SocketShutdown.Both); } catch { /* best-effort */ }
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
