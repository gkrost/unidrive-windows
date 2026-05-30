namespace Unidrive.Ipc;

/// <summary>Reply of the <c>daemon.status</c> verb.</summary>
public sealed record DaemonStatus(long UptimeMs, int ClientsConnected, bool RefreshInFlight, string? RefreshJobId);

/// <summary>
/// Thrown when the daemon returns <c>{"ok":false,"error":"&lt;token&gt;"}</c>. The two load-bearing
/// tokens (<c>unknown_path</c>, <c>not_found</c>) map to a not-found result; everything else is a
/// retryable IO error (see the engine's HydrationError contract).
/// </summary>
public sealed class IpcException : Exception
{
    public string Verb { get; }
    public string? ErrorToken { get; }

    /// <summary>True for the tokens that the engine treats as ENOENT-equivalent.</summary>
    public bool IsNotFound => ErrorToken is "unknown_path" or "not_found";

    public IpcException(string verb, string? errorToken)
        : base($"daemon rejected '{verb}': {errorToken ?? "unknown error"}")
    {
        Verb = verb;
        ErrorToken = errorToken;
    }
}
